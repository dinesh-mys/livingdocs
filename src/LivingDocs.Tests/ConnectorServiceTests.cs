using System.Net;
using System.Net.Http;
using System.Text;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

/// <summary>Tests for Slack, Teams, and Email connector services.</summary>
public class SlackConnectorServiceTests
{
    [Fact]
    public async Task FetchChunksAsync_MissingToken_Throws()
    {
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", null);
        var svc = new SlackConnectorService(new HttpClient());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.FetchChunksAsync("C1234"));
    }

    [Fact]
    public async Task FetchChunksAsync_ApiError_Throws()
    {
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-test");

        // Stub both API calls: conversations.info → ok:false, conversations.history → ok:false
        var handler = new StubHttpHandler(new Dictionary<string, string>
        {
            ["conversations.info"]    = """{"ok":true,"channel":{"name":"general"}}""",
            ["conversations.history"] = """{"ok":false,"error":"not_in_channel"}"""
        });

        var svc = new SlackConnectorService(new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.FetchChunksAsync("C1234"));
    }

    [Fact]
    public async Task FetchChunksAsync_ValidMessages_ReturnsChunks()
    {
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-test");

        var messagesJson = """
            {
              "ok": true,
              "messages": [
                {
                  "type": "message",
                  "user": "U001",
                  "text": "We decided to switch to JWT tokens because the old session approach had race conditions.",
                  "ts": "1700000001.000001",
                  "thread_ts": "1700000001.000001"
                },
                {
                  "type": "message",
                  "user": "U002",
                  "text": "Good call. The JWT secret rotation should happen every 30 days.",
                  "ts": "1700000002.000001",
                  "thread_ts": "1700000001.000001"
                },
                {
                  "type": "message",
                  "subtype": "bot_message",
                  "text": "Deployment succeeded",
                  "ts": "1700000003.000001",
                  "thread_ts": "1700000003.000001"
                },
                {
                  "type": "message",
                  "user": "U003",
                  "text": "short",
                  "ts": "1700000004.000001",
                  "thread_ts": "1700000004.000001"
                }
              ]
            }
            """;

        var handler = new StubHttpHandler(new Dictionary<string, string>
        {
            ["conversations.info"]    = """{"ok":true,"channel":{"name":"engineering"}}""",
            ["conversations.history"] = messagesJson
        });

        var svc    = new SlackConnectorService(new HttpClient(handler));
        var chunks = await svc.FetchChunksAsync("C1234");

        // bot_message and "short" are filtered; 2 user messages share a thread_ts → 1 chunk
        Assert.Single(chunks);
        Assert.Equal("slack://C1234", chunks[0].FilePath);
        Assert.Equal("slack",          chunks[0].Language);
        Assert.Contains("JWT",         chunks[0].Content);
    }

    [Fact]
    public async Task FetchChunksAsync_BotAndShortMessages_AreFiltered()
    {
        Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-test");

        var messagesJson = """
            {
              "ok": true,
              "messages": [
                { "type": "message", "subtype": "bot_message", "text": "CI passed ✓", "ts": "1.0", "thread_ts": "1.0" },
                { "type": "message", "subtype": "channel_join", "text": "Alice joined",  "ts": "2.0", "thread_ts": "2.0" },
                { "type": "message", "user": "U1", "text": "ok", "ts": "3.0", "thread_ts": "3.0" }
              ]
            }
            """;

        var handler = new StubHttpHandler(new Dictionary<string, string>
        {
            ["conversations.info"]    = """{"ok":true,"channel":{"name":"general"}}""",
            ["conversations.history"] = messagesJson
        });

        var svc    = new SlackConnectorService(new HttpClient(handler));
        var chunks = await svc.FetchChunksAsync("C1234");

        Assert.Empty(chunks);
    }
}

public class TeamsConnectorServiceTests
{
    [Fact]
    public async Task FetchChunksAsync_MissingCredentials_Throws()
    {
        Environment.SetEnvironmentVariable("TEAMS_TENANT_ID",     null);
        Environment.SetEnvironmentVariable("TEAMS_CLIENT_ID",     null);
        Environment.SetEnvironmentVariable("TEAMS_CLIENT_SECRET", null);

        var svc = new TeamsConnectorService(new HttpClient());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.FetchChunksAsync("team1", "channel1"));
    }

    [Fact]
    public async Task FetchChunksAsync_ValidMessages_ReturnsChunks()
    {
        Environment.SetEnvironmentVariable("TEAMS_TENANT_ID",     "tenant-x");
        Environment.SetEnvironmentVariable("TEAMS_CLIENT_ID",     "client-x");
        Environment.SetEnvironmentVariable("TEAMS_CLIENT_SECRET", "secret-x");

        var tokenResp = """{"access_token":"test-token","token_type":"Bearer","expires_in":3600}""";
        var messagesJson = """
            {
              "value": [
                {
                  "id": "msg1",
                  "createdDateTime": "2024-01-15T10:00:00Z",
                  "from": { "user": { "displayName": "Alice Chen" } },
                  "body": { "contentType": "text", "content": "We should migrate the payment service to async processing. The current sync approach blocks the UI thread and causes timeouts on large transactions." }
                },
                {
                  "id": "msg2",
                  "createdDateTime": "2024-01-15T10:05:00Z",
                  "from": { "user": { "displayName": "Bob Smith" } },
                  "body": { "contentType": "text", "content": "ok" }
                }
              ]
            }
            """;

        var handler = new StubHttpHandler(new Dictionary<string, string>
        {
            ["oauth2"]   = tokenResp,
            ["messages"] = messagesJson
        });

        var svc    = new TeamsConnectorService(new HttpClient(handler));
        var chunks = await svc.FetchChunksAsync("team1", "channel1");

        // "ok" is too short (< 30 chars) — only 1 chunk
        Assert.Single(chunks);
        Assert.Equal("teams://team1/channel1", chunks[0].FilePath);
        Assert.Equal("teams", chunks[0].Language);
        Assert.Contains("Alice Chen", chunks[0].Content);
    }
}

public class EmailConnectorServiceTests
{
    [Fact]
    public async Task FetchChunksAsync_MissingSettings_Throws()
    {
        Environment.SetEnvironmentVariable("IMAP_HOST",     null);
        Environment.SetEnvironmentVariable("IMAP_USERNAME", null);
        Environment.SetEnvironmentVariable("IMAP_PASSWORD", null);

        var svc = new EmailConnectorService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.FetchChunksAsync());
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;

    public StubHttpHandler(Dictionary<string, string> responses) => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";

        foreach (var (key, body) in _responses)
        {
            if (url.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
    }
}
