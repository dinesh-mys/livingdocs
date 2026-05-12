using System.Net.Http.Json;
using System.Text.Json;
using LivingDocs.Core.Claude;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private const string ApiUrl   = "https://api.anthropic.com/v1/messages";
    private const string Model    = "claude-sonnet-4-6";
    private const int    MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaudeService(HttpClient httpClient, string? apiKey = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        var key = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException(
                "Anthropic API key not set. Provide via constructor or ANTHROPIC_API_KEY env var.");

        if (!_httpClient.DefaultRequestHeaders.Contains("x-api-key"))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", key);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    public async Task<string> CompleteAsync(string prompt, int maxTokens = 1024)
    {
        var request = new ClaudeRequest(
            Model:     Model,
            MaxTokens: maxTokens,
            Messages:  [new ClaudeMessage("user", prompt)]
        );

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(ApiUrl, request);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOptions);
                    return body?.Content.FirstOrDefault(c => c.Type == "text")?.Text
                        ?? string.Empty;
                }

                // 429 rate-limit or 5xx — retry with backoff
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    if (attempt < MaxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }

                // 4xx non-retriable
                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Claude API error {(int)response.StatusCode}: {error}");
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        throw new HttpRequestException($"Claude API failed after {MaxRetries} attempts.");
    }

    public async Task<string> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc)
    {
        var prompt = $"""
            You are a documentation assistant. A developer changed code and the existing
            documentation may now be stale. Suggest a concise updated doc comment only —
            no explanation, no markdown fences, just the updated comment text.

            FILE: {change.FilePath}
            SYMBOL: {existingDoc.ParentSymbol ?? "unknown"}
            LANGUAGE: {existingDoc.Language}

            EXISTING DOCUMENTATION:
            {existingDoc.Content}

            CODE CHANGE (diff):
            {change.Diff}

            Write the updated documentation comment:
            """;

        return await CompleteAsync(prompt, maxTokens: 512);
    }

    public async Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs)
    {
        var docList = docs.ToList();

        var context = new System.Text.StringBuilder();
        foreach (var doc in docList)
        {
            var symbol = doc.ParentSymbol is not null ? $" ({doc.ParentSymbol})" : string.Empty;
            context.AppendLine($"[{doc.FilePath}:{doc.LineNumber}{symbol}]");
            context.AppendLine(doc.Content);
            context.AppendLine();
        }

        var prompt = $"""
            You are a documentation assistant. Answer the question below using only the
            documentation comments provided. If the answer is not in the docs, say so clearly.
            Be concise and cite the file and symbol where relevant.

            DOCUMENTATION:
            {context}

            QUESTION: {question}
            """;

        return await CompleteAsync(prompt, maxTokens: 1024);
    }
}
