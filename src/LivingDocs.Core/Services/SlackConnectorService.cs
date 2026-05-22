using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class SlackConnectorService : ISlackConnectorService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SlackConnectorService(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<DocChunk>> FetchChunksAsync(string channelId, int limit = 200)
    {
        var token = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("SLACK_BOT_TOKEN is not set.");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var channelName = await GetChannelNameAsync(channelId);
        var messages    = await FetchHistoryAsync(channelId, limit);
        var threads     = GroupIntoThreads(messages);

        var chunks = new List<DocChunk>();
        int seq    = 0;

        foreach (var thread in threads)
        {
            var text = BuildThreadText(thread, channelName);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var ts = thread[0].Ts;
            var dt = TsToDateTime(ts);

            chunks.Add(new DocChunk
            {
                Id            = Guid.NewGuid(),
                FilePath      = $"slack://{channelId}",
                Language      = "slack",
                Content       = text,
                ParentSymbol  = $"#{channelName} thread {ts}",
                LineNumber    = seq++,
                LastUpdated   = dt
            });
        }

        return chunks;
    }

    // ── Slack API helpers ─────────────────────────────────────────────────

    private async Task<string> GetChannelNameAsync(string channelId)
    {
        try
        {
            var url  = $"https://slack.com/api/conversations.info?channel={channelId}";
            var json = await _http.GetStringAsync(url);
            var doc  = JsonDocument.Parse(json).RootElement;

            if (!doc.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return channelId;
            return doc.GetProperty("channel").TryGetProperty("name", out var name)
                ? name.GetString() ?? channelId
                : channelId;
        }
        catch { return channelId; }
    }

    private async Task<List<SlackMessage>> FetchHistoryAsync(string channelId, int limit)
    {
        var url  = $"https://slack.com/api/conversations.history?channel={channelId}&limit={Math.Min(limit, 1000)}";
        var json = await _http.GetStringAsync(url);
        var doc  = JsonDocument.Parse(json).RootElement;

        if (!doc.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = doc.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            throw new InvalidOperationException($"Slack API error: {error}");
        }

        var result = new List<SlackMessage>();
        foreach (var msg in doc.GetProperty("messages").EnumerateArray())
        {
            var type    = msg.TryGetProperty("type",    out var t) ? t.GetString() : null;
            var subtype = msg.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            var text    = msg.TryGetProperty("text",    out var tx) ? tx.GetString() ?? "" : "";
            var user    = msg.TryGetProperty("user",    out var u) ? u.GetString() ?? "" : "";
            var ts      = msg.TryGetProperty("ts",      out var ts_) ? ts_.GetString() ?? "" : "";
            var threadTs = msg.TryGetProperty("thread_ts", out var tts) ? tts.GetString() ?? ts : ts;

            if (type != "message") continue;
            if (subtype is "bot_message" or "channel_join" or "channel_leave") continue;
            if (text.Length < 30) continue;

            result.Add(new SlackMessage(user, text, ts, threadTs));
        }

        return result;
    }

    private static List<List<SlackMessage>> GroupIntoThreads(List<SlackMessage> messages)
    {
        var byThread = new Dictionary<string, List<SlackMessage>>();

        foreach (var msg in messages)
        {
            if (!byThread.TryGetValue(msg.ThreadTs, out var thread))
            {
                thread = [];
                byThread[msg.ThreadTs] = thread;
            }
            thread.Add(msg);
        }

        return byThread.Values
            .OrderByDescending(t => t[0].Ts)
            .ToList();
    }

    private static string BuildThreadText(List<SlackMessage> thread, string channelName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[#{channelName}]");

        foreach (var msg in thread)
        {
            var author = string.IsNullOrEmpty(msg.User) ? "Unknown" : msg.User;
            sb.AppendLine($"{author}: {CleanSlackMarkup(msg.Text)}");
        }

        return sb.ToString().Trim();
    }

    private static string CleanSlackMarkup(string text)
    {
        // Remove <@USER> mentions, <#CHANNEL> references, <url|label> links
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<@[A-Z0-9]+>", "@user");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<#[A-Z0-9]+\|([^>]+)>", "#$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<([^|>]+)\|([^>]+)>", "$2");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<([^>]+)>", "$1");
        return text;
    }

    private static DateTime TsToDateTime(string ts)
    {
        if (double.TryParse(ts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds((long)unix).UtcDateTime;
        return DateTime.UtcNow;
    }

    private record SlackMessage(string User, string Text, string Ts, string ThreadTs);
}
