using System.Net.Http.Json;
using System.Text.Json;
using LivingDocs.Core.Claude;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

/// <summary>Calls the Anthropic Messages API. Retries up to 3 times with exponential backoff on rate-limit and server errors.</summary>
public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private const string ApiUrl   = "https://api.anthropic.com/v1/messages";
    private const string DefaultModel = "claude-sonnet-4-6";
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

    public async Task<string> CompleteAsync(string prompt, int maxTokens = 1024, string? model = null)
    {
        var request = new ClaudeRequest(
            Model:     model ?? DefaultModel,
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

    public async Task<DocSuggestion> SuggestDocUpdateAsync(
        ChangeEvent change, DocChunk existingDoc, string? symbolContext = null)
    {
        var contextBlock = symbolContext is not null
            ? $"\nCURRENT CODE (surrounding context):\n{symbolContext}\n"
            : string.Empty;

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
            {contextBlock}
            Write the updated documentation comment, then on the very last line write:
            CONFIDENCE: <score between 0.0 and 1.0>
            where 1.0 means you are certain the suggestion is correct and 0.0 means you are guessing.
            """;

        var raw        = await CompleteAsync(prompt, maxTokens: 600);
        var (text, confidence) = ParseConfidence(raw);
        return new DocSuggestion(text, confidence, NeedsReview: confidence < ConfidenceThreshold);
    }

    private const float ConfidenceThreshold = 0.6f;

    private static (string text, float confidence) ParseConfidence(string raw)
    {
        var lines = raw.TrimEnd().Split('\n');

        for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 3); i--)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase))
            {
                var valueStr = line["CONFIDENCE:".Length..].Trim();
                if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var score))
                {
                    var suggestion = string.Join('\n', lines[..i]).TrimEnd();
                    return (suggestion, Math.Clamp(score, 0f, 1f));
                }
            }
        }

        // Claude didn't emit a score — return the full text with neutral confidence.
        return (raw.TrimEnd(), 0.5f);
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
