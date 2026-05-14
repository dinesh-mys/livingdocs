using System.Net;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Models;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class ClaudeServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static ClaudeService BuildSut(
        HttpStatusCode status,
        string responseBody,
        int callCount = 0)
    {
        var handler = new FakeHttpHandler(status, responseBody, callCount);
        var client  = new HttpClient(handler);
        return new ClaudeService(client, apiKey: "test-key");
    }

    private static string OkBody(string text) => JsonSerializer.Serialize(new
    {
        content = new[] { new { type = "text", text } }
    });

    // ── CompleteAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_SuccessResponse_ReturnsText()
    {
        var sut = BuildSut(HttpStatusCode.OK, OkBody("Updated doc comment."));

        var result = await sut.CompleteAsync("Summarise this change.");

        Assert.Equal("Updated doc comment.", result);
    }

    [Fact]
    public async Task CompleteAsync_EmptyContentArray_ReturnsEmptyString()
    {
        var body = JsonSerializer.Serialize(new { content = Array.Empty<object>() });
        var sut  = BuildSut(HttpStatusCode.OK, body);

        var result = await sut.CompleteAsync("prompt");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task CompleteAsync_NonRetriable4xx_ThrowsHttpRequestException()
    {
        var sut = BuildSut(HttpStatusCode.BadRequest, "bad request");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.CompleteAsync("prompt"));
    }

    [Fact]
    public async Task CompleteAsync_RateLimitThenSuccess_RetriesAndReturnsText()
    {
        // First call returns 429, second returns 200
        var handler = new SequentialFakeHttpHandler(
        [
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.OK,              OkBody("Retried successfully."))
        ]);
        var sut = new ClaudeService(new HttpClient(handler), apiKey: "test-key");

        var result = await sut.CompleteAsync("prompt");

        Assert.Equal("Retried successfully.", result);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_AllRetriesFail_ThrowsHttpRequestException()
    {
        var sut = BuildSut(HttpStatusCode.ServiceUnavailable, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.CompleteAsync("prompt"));
    }

    // ── SuggestDocUpdateAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SuggestDocUpdateAsync_BuildsPromptAndReturnsClaudeText()
    {
        var sut = BuildSut(HttpStatusCode.OK, OkBody("/// Calculates tax at 20%."));

        var change = new ChangeEvent
        {
            FilePath   = "Tax.cs",
            Diff       = "@@ -1 +1 @@ return amount * 0.2;",
            CommitHash = "abc123",
            ChangedBy  = "dev",
            Timestamp  = DateTime.UtcNow,
            ChangeType = ChangeType.Modified
        };

        var doc = new DocChunk
        {
            FilePath     = "Tax.cs",
            Content      = "Calculates tax at 15%.",
            Language     = "csharp",
            ParentSymbol = "CalculateTax"
        };

        var result = await sut.SuggestDocUpdateAsync(change, doc);

        Assert.Equal("/// Calculates tax at 20%.", result.Suggestion);
    }

    // ── QueryDocsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task QueryDocsAsync_WithDocs_SendsPromptAndReturnsAnswer()
    {
        var sut = BuildSut(HttpStatusCode.OK, OkBody("Tax.cs handles tax at 20%."));

        var docs = new[]
        {
            new DocChunk { FilePath = "Tax.cs", LineNumber = 5, Content = "Calculates tax at 20%.", ParentSymbol = "CalculateTax", Language = "csharp" }
        };

        var result = await sut.QueryDocsAsync("What does CalculateTax do?", docs);

        Assert.Equal("Tax.cs handles tax at 20%.", result);
    }

    [Fact]
    public async Task QueryDocsAsync_NoDocs_ReturnsAnswer()
    {
        var sut = BuildSut(HttpStatusCode.OK, OkBody("No docs found."));

        var result = await sut.QueryDocsAsync("What does this repo do?", []);

        Assert.Equal("No docs found.", result);
    }

    // ── Missing API key ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_NoApiKey_ThrowsInvalidOperationException()
    {
        // Ensure env var is not set in test environment
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);

        Assert.Throws<InvalidOperationException>(
            () => new ClaudeService(new HttpClient()));
    }
}

// ── Fake HTTP handlers ────────────────────────────────────────────────────

file sealed class FakeHttpHandler(HttpStatusCode status, string body, int _) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
}

file sealed class SequentialFakeHttpHandler(
    IReadOnlyList<(HttpStatusCode Status, string Body)> responses) : HttpMessageHandler
{
    private int _index;
    public int CallCount => _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var (status, body) = responses[Math.Min(_index++, responses.Count - 1)];
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}
