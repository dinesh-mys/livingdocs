using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using Microsoft.AspNetCore.Http;

namespace LivingDocs.Tests;

public class SlackActionsHandlerTests
{
    private const string SigningSecret = "slack-test-secret";

    private static DefaultHttpContext BuildContext(string body, bool validSignature = true, bool recentTimestamp = true)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var timestamp = recentTimestamp
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()
            : "0"; // very old

        var baseString = $"v0:{timestamp}:{body}";
        var sig = validSignature
            ? "v0=" + Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(SigningSecret),
                    Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant()
            : "v0=deadbeef";

        var ctx = new DefaultHttpContext();
        ctx.Request.Body                               = new MemoryStream(bodyBytes);
        ctx.Request.Headers["X-Slack-Request-Timestamp"] = timestamp;
        ctx.Request.Headers["X-Slack-Signature"]         = sig;
        return ctx;
    }

    private static readonly IClaudeService StubClaude = new StubClaudeService();
    private static readonly IGitScannerService StubScanner = new StubScannerService();
    private static readonly IDocExtractorService StubExtractor = new StubExtractorService();

    private sealed class StubClaudeService : IClaudeService
    {
        public Task<string> CompleteAsync(string prompt, int maxTokens = 1024, string? model = null) => Task.FromResult(string.Empty);
        public Task<DocSuggestion> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc, string? symbolContext = null)
            => Task.FromResult(new DocSuggestion("/// Updated doc.", 0.9f, NeedsReview: false));
        public Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs) => Task.FromResult(string.Empty);
        public Task<string> AnalysePrDiffAsync(string diff) => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<AffectedDoc>> FindAffectedDocsAsync(string s, IEnumerable<string> f, IEnumerable<DocCandidate> c)
            => Task.FromResult<IReadOnlyList<AffectedDoc>>([]);
    }

    private sealed class StubScannerService : IGitScannerService
    {
        public Task<IEnumerable<ChangeEvent>> ScanAsync(string repoPath, string? sinceCommit = null)
            => Task.FromResult<IEnumerable<ChangeEvent>>([]);
        public Task<string?> GetSymbolContextAsync(string repoPath, string filePath, string symbolName, int contextLines = 30)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubExtractorService : IDocExtractorService
    {
        public Task<IEnumerable<DocChunk>> ExtractAsync(string filePath, string content)
            => Task.FromResult<IEnumerable<DocChunk>>([]);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_MissingSigningSecret_Returns401()
    {
        Environment.SetEnvironmentVariable("SLACK_SIGNING_SECRET", null);
        var ctx    = BuildContext("payload=%7B%7D");
        var result = await SlackActionsHandler.HandleAsync(ctx, StubClaude, StubScanner, StubExtractor);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_WrongSignature_Returns401()
    {
        Environment.SetEnvironmentVariable("SLACK_SIGNING_SECRET", SigningSecret);
        var ctx    = BuildContext("payload=%7B%7D", validSignature: false);
        var result = await SlackActionsHandler.HandleAsync(ctx, StubClaude, StubScanner, StubExtractor);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_StaleTimestamp_Returns401()
    {
        Environment.SetEnvironmentVariable("SLACK_SIGNING_SECRET", SigningSecret);
        var ctx    = BuildContext("payload=%7B%7D", validSignature: true, recentTimestamp: false);
        var result = await SlackActionsHandler.HandleAsync(ctx, StubClaude, StubScanner, StubExtractor);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_ValidSignature_NoResponseUrl_Returns200()
    {
        Environment.SetEnvironmentVariable("SLACK_SIGNING_SECRET", SigningSecret);

        var payload = JsonSerializer.Serialize(new
        {
            type         = "block_actions",
            response_url = (string?)null,
            actions      = Array.Empty<object>()
        });
        var body   = "payload=" + Uri.EscapeDataString(payload);
        var ctx    = BuildContext(body);
        var result = await SlackActionsHandler.HandleAsync(ctx, StubClaude, StubScanner, StubExtractor);

        // Should not be 401
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_ValidSignature_UnknownAction_Returns200()
    {
        Environment.SetEnvironmentVariable("SLACK_SIGNING_SECRET", SigningSecret);

        var payload = JsonSerializer.Serialize(new
        {
            type         = "block_actions",
            response_url = "https://hooks.slack.com/actions/test",
            actions      = new[] { new { action_id = "other_action", value = "{}" } }
        });
        var body   = "payload=" + Uri.EscapeDataString(payload);
        var ctx    = BuildContext(body);
        var result = await SlackActionsHandler.HandleAsync(ctx, StubClaude, StubScanner, StubExtractor);

        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }
}
