using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class TeamsConnectorService : ITeamsConnectorService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public TeamsConnectorService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<DocChunk>> FetchChunksAsync(
        string teamId, string channelId, int limit = 200)
    {
        var tenantId     = Environment.GetEnvironmentVariable("TEAMS_TENANT_ID");
        var clientId     = Environment.GetEnvironmentVariable("TEAMS_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("TEAMS_CLIENT_SECRET");

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                "TEAMS_TENANT_ID, TEAMS_CLIENT_ID, and TEAMS_CLIENT_SECRET must all be set.");

        var token = await GetAccessTokenAsync(tenantId, clientId, clientSecret);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var messages = await FetchMessagesAsync(teamId, channelId, Math.Min(limit, 50));
        var chunks   = new List<DocChunk>();
        int seq      = 0;

        foreach (var msg in messages)
        {
            var text = BuildMessageText(msg);
            if (string.IsNullOrWhiteSpace(text)) continue;

            chunks.Add(new DocChunk
            {
                Id           = Guid.NewGuid(),
                FilePath     = $"teams://{teamId}/{channelId}",
                Language     = "teams",
                Content      = text,
                ParentSymbol = $"Teams message {msg.Id}",
                LineNumber   = seq++,
                LastUpdated  = msg.CreatedDateTime
            });
        }

        return chunks;
    }

    // ── Graph API helpers ─────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(
        string tenantId, string clientId, string clientSecret)
    {
        var url  = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var body = new FormUrlEncodedContent([
            new("grant_type",    "client_credentials"),
            new("client_id",     clientId),
            new("client_secret", clientSecret),
            new("scope",         "https://graph.microsoft.com/.default")
        ]);

        var resp = await _http.PostAsync(url, body);
        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Teams auth failed: {json}");

        var doc = JsonDocument.Parse(json).RootElement;
        return doc.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("No access_token in response.");
    }

    private async Task<List<TeamsMessage>> FetchMessagesAsync(
        string teamId, string channelId, int top)
    {
        var url  = $"https://graph.microsoft.com/v1.0/teams/{teamId}/channels/{channelId}/messages?$top={top}";
        var json = await _http.GetStringAsync(url);
        var doc  = JsonDocument.Parse(json).RootElement;

        var result = new List<TeamsMessage>();
        foreach (var msg in doc.GetProperty("value").EnumerateArray())
        {
            var id          = msg.TryGetProperty("id",              out var i)  ? i.GetString()  ?? "" : "";
            var createdRaw  = msg.TryGetProperty("createdDateTime", out var dt) ? dt.GetString() ?? "" : "";
            var fromDisplay = msg.TryGetProperty("from",            out var fr)
                && fr.TryGetProperty("user", out var usr)
                && usr.TryGetProperty("displayName", out var dn)
                ? dn.GetString() ?? "Unknown"
                : "Unknown";

            string bodyText;
            if (msg.TryGetProperty("body", out var body))
            {
                var contentType = body.TryGetProperty("contentType", out var ct) ? ct.GetString() : "text";
                var content     = body.TryGetProperty("content",     out var c)  ? c.GetString() ?? "" : "";
                bodyText = contentType == "html" ? StripHtml(content) : content;
            }
            else bodyText = "";

            if (bodyText.Length < 30) continue;

            var created = DateTime.TryParse(createdRaw, out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
            result.Add(new TeamsMessage(id, fromDisplay, bodyText, created));
        }

        return result;
    }

    private static string BuildMessageText(TeamsMessage msg) =>
        $"[Teams] {msg.From}: {msg.Body}".Trim();

    private static string StripHtml(string html)
    {
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return System.Text.RegularExpressions.Regex.Replace(html, @"\s{2,}", " ").Trim();
    }

    private record TeamsMessage(string Id, string From, string Body, DateTime CreatedDateTime);
}
