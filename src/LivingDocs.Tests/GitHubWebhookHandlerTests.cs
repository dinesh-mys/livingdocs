using System.Security.Cryptography;
using System.Text;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using Microsoft.AspNetCore.Http;

namespace LivingDocs.Tests;

public class GitHubWebhookHandlerTests : IDisposable
{
    private const string Secret = "test-webhook-secret";

    public GitHubWebhookHandlerTests() =>
        Environment.SetEnvironmentVariable("GITHUB_WEBHOOK_SECRET", Secret);

    public void Dispose() =>
        Environment.SetEnvironmentVariable("GITHUB_WEBHOOK_SECRET", null);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeSignature(byte[] body) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body)).ToLowerInvariant();

    private static DefaultHttpContext BuildContext(
        string json, string eventType, string? signature = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var sig  = signature ?? ComputeSignature(body);

        var ctx = new DefaultHttpContext();
        ctx.Request.Body                              = new MemoryStream(body);
        ctx.Request.Headers["X-GitHub-Event"]         = eventType;
        ctx.Request.Headers["X-Hub-Signature-256"]    = sig;
        return ctx;
    }

    private static readonly IStaleDocDetectorService StubDetector = new NullDetector();

    private sealed class NullDetector : IStaleDocDetectorService
    {
        public Task<ScanResult> DetectAsync(string repoPath) =>
            Task.FromResult(new ScanResult { RepoPath = repoPath });
    }

    // ── Signature validation ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_MissingSignature_Returns401()
    {
        var ctx = BuildContext("{}", "ping", signature: "");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_WrongSignature_Returns401()
    {
        var ctx = BuildContext("{}", "ping", signature: "sha256=deadbeef");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task HandleAsync_ValidSignature_DoesNotReturn401()
    {
        var ctx    = BuildContext("{}", "ping");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.UnauthorizedHttpResult>(result);
    }

    // ── Event routing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_PingEvent_ReturnsIgnored()
    {
        var ctx    = BuildContext("{}", "ping");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        var ok     = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(result);
        Assert.Equal("event ignored", ok.Value);
    }

    [Fact]
    public async Task HandleAsync_PushEvent_ReturnsIgnored()
    {
        var ctx    = BuildContext("{}", "push");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        var ok     = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(result);
        Assert.Equal("event ignored", ok.Value);
    }

    [Fact]
    public async Task HandleAsync_PrNotMerged_ReturnsIgnored()
    {
        var json = """
            {
              "action": "closed",
              "pull_request": { "number": 1, "merged": false },
              "repository": { "name": "repo", "owner": { "login": "owner" } }
            }
            """;
        var ctx    = BuildContext(json, "pull_request");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        var ok     = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(result);
        Assert.Equal("event ignored", ok.Value);
    }

    [Fact]
    public async Task HandleAsync_PrOpenedNotClosed_ReturnsIgnored()
    {
        var json = """
            {
              "action": "opened",
              "pull_request": { "number": 1, "merged": true },
              "repository": { "name": "repo", "owner": { "login": "owner" } }
            }
            """;
        var ctx    = BuildContext(json, "pull_request");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        var ok     = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(result);
        Assert.Equal("event ignored", ok.Value);
    }

    [Fact]
    public async Task HandleAsync_MergedPr_NoRepoPath_ReturnsSkipped()
    {
        Environment.SetEnvironmentVariable("LIVINGDOCS_REPO_PATH", null);

        var json = """
            {
              "action": "closed",
              "pull_request": { "number": 1, "merged": true },
              "repository": { "name": "repo", "owner": { "login": "owner" } }
            }
            """;
        var ctx    = BuildContext(json, "pull_request");
        var result = await GitHubWebhookHandler.HandleAsync(ctx, StubDetector);
        var ok     = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<string>>(result);
        Assert.Contains("LIVINGDOCS_REPO_PATH", ok.Value);
    }
}
