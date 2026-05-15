using System.Net.Http.Json;
using System.Text.Json;
using LivingDocs.Core.Claude;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

/// <summary>
/// Calls Anthropic Messages API by default. If AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY,
/// and AZURE_OPENAI_DEPLOYMENT are set, routes to Azure OpenAI (chat/completions) instead.
/// Retries up to 3 times with exponential backoff on rate-limit and server errors.
/// </summary>
public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly bool _useAzure;
    private readonly string? _azureEndpoint;
    private const string AnthropicUrl  = "https://api.anthropic.com/v1/messages";
    private const string DefaultModel  = "claude-sonnet-4-6";
    private const int    MaxRetries    = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaudeService(HttpClient httpClient, string? apiKey = null)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);

        var azureEndpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureKey        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        if (!string.IsNullOrWhiteSpace(azureEndpoint) &&
            !string.IsNullOrWhiteSpace(azureKey) &&
            !string.IsNullOrWhiteSpace(azureDeployment))
        {
            _useAzure     = true;
            _azureEndpoint = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{azureDeployment}" +
                             "/chat/completions?api-version=2024-08-01-preview";

            if (!_httpClient.DefaultRequestHeaders.Contains("api-key"))
                _httpClient.DefaultRequestHeaders.Add("api-key", azureKey);
        }
        else
        {
            var key = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                ?? throw new InvalidOperationException(
                    "No LLM configured. Set ANTHROPIC_API_KEY or AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT.");

            if (!_httpClient.DefaultRequestHeaders.Contains("x-api-key"))
            {
                _httpClient.DefaultRequestHeaders.Add("x-api-key", key);
                _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
        }
    }

    public async Task<string> CompleteAsync(string prompt, int maxTokens = 1024, string? model = null)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = _useAzure
                    ? await SendAzureAsync(prompt, maxTokens)
                    : await SendAnthropicAsync(prompt, maxTokens, model);

                if (response.IsSuccessStatusCode)
                    return _useAzure
                        ? await ParseAzureResponseAsync(response)
                        : await ParseAnthropicResponseAsync(response);

                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    if (attempt < MaxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }

                var error = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"LLM API error {(int)response.StatusCode}: {error}");
            }
            catch (TaskCanceledException) when (attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        throw new HttpRequestException($"LLM API failed after {MaxRetries} attempts.");
    }

    private Task<HttpResponseMessage> SendAnthropicAsync(string prompt, int maxTokens, string? model)
    {
        var request = new ClaudeRequest(
            Model:     model ?? DefaultModel,
            MaxTokens: maxTokens,
            Messages:  [new ClaudeMessage("user", prompt)]
        );
        return _httpClient.PostAsJsonAsync(AnthropicUrl, request);
    }

    private Task<HttpResponseMessage> SendAzureAsync(string prompt, int maxTokens)
    {
        var body = new
        {
            messages  = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokens
        };
        return _httpClient.PostAsJsonAsync(_azureEndpoint, body);
    }

    private static async Task<string> ParseAnthropicResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOptions);
        return body?.Content.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;
    }

    private static async Task<string> ParseAzureResponseAsync(HttpResponseMessage response)
    {
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
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

        var raw = await CompleteAsync(prompt, maxTokens: 600);
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

        return (raw.TrimEnd(), 0.5f);
    }

    public async Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs)
    {
        var context = new System.Text.StringBuilder();
        foreach (var doc in docs)
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
