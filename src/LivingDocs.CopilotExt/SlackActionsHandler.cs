using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

/// <summary>
/// Handles Slack interactive component callbacks at /api/slack/actions.
/// Validates the Slack signing secret, then dispatches based on action_id.
/// "autodraft_doc" — runs SuggestDocUpdateAsync and posts the result back to Slack.
/// Set SLACK_SIGNING_SECRET in the environment.
/// </summary>
public static class SlackActionsHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IResult> HandleAsync(
        HttpContext ctx,
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var bodyBytes = ms.ToArray();
        var bodyText  = Encoding.UTF8.GetString(bodyBytes);

        if (!ValidateSlackSignature(ctx.Request, bodyBytes))
            return Results.Unauthorized();

        // Slack sends actions as application/x-www-form-urlencoded with a "payload" field
        var form        = WebUtility.UrlDecode(bodyText);
        var payloadJson = ExtractPayloadField(form);
        if (payloadJson is null)
            return Results.BadRequest("missing payload");

        JsonElement root;
        try { root = JsonDocument.Parse(payloadJson).RootElement; }
        catch { return Results.BadRequest("invalid payload JSON"); }

        var responseUrl = root.TryGetProperty("response_url", out var ru) ? ru.GetString() : null;
        if (string.IsNullOrWhiteSpace(responseUrl))
            return Results.Ok(); // No way to respond — ack and move on

        if (!root.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
            return Results.Ok();

        foreach (var action in actionsEl.EnumerateArray())
        {
            var actionId = action.TryGetProperty("action_id", out var id) ? id.GetString() : null;
            var value    = action.TryGetProperty("value",     out var v)  ? v.GetString()  : null;

            if (actionId == "autodraft_doc" && !string.IsNullOrEmpty(value))
            {
                AutoDraftAction? draftAction;
                try { draftAction = JsonSerializer.Deserialize<AutoDraftAction>(value, JsonOpts); }
                catch { continue; }

                if (draftAction is null) continue;

                // Ack Slack immediately, then generate async — Claude call may take >3s
                _ = Task.Run(() => GenerateAndPostAsync(
                    claude, scanner, extractor, draftAction, responseUrl));
            }
        }

        return Results.Ok();
    }

    private static async Task GenerateAndPostAsync(
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor,
        AutoDraftAction action,
        string responseUrl)
    {
        var message = await BuildDraftMessageAsync(claude, scanner, extractor, action);
        await PostToSlackAsync(responseUrl, message);
    }

    private static async Task<string> BuildDraftMessageAsync(
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor,
        AutoDraftAction action)
    {
        var fullPath = Path.Combine(action.RepoPath, action.FilePath);
        if (!File.Exists(fullPath))
            return $"⚠️ File not found: `{action.FilePath}`";

        var content = await File.ReadAllTextAsync(fullPath);
        var chunks  = (await extractor.ExtractAsync(action.FilePath, content)).ToList();

        if (chunks.Count == 0)
            return $"No documentation comments found in `{action.FilePath}` — nothing to draft.";

        var changes = (await scanner.ScanAsync(action.RepoPath))
            .Where(c => string.Equals(c.FilePath, action.FilePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .ToList();

        if (changes.Count == 0)
            return $"No recent git changes found for `{action.FilePath}`.";

        var latestChange = changes[0];
        var sb = new StringBuilder();
        sb.AppendLine($"*Auto-drafted doc update for `{action.FilePath}` (PR #{action.PrNumber})*");
        sb.AppendLine();

        foreach (var chunk in chunks.Take(3)) // cap at 3 symbols to keep Slack message readable
        {
            var symbolContext = chunk.ParentSymbol is not null
                ? await scanner.GetSymbolContextAsync(action.RepoPath, action.FilePath, chunk.ParentSymbol)
                : null;

            var result    = await claude.SuggestDocUpdateAsync(latestChange, chunk, symbolContext);
            var symbol    = chunk.ParentSymbol ?? "unknown";
            var confidence = result.NeedsReview ? $"⚠️ {result.Confidence:P0} — review before applying" : $"✅ {result.Confidence:P0}";

            sb.AppendLine($"*{symbol}*  |  Confidence: {confidence}");
            sb.AppendLine("```");
            sb.AppendLine(result.Suggestion.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (chunks.Count > 3)
            sb.AppendLine($"_...{chunks.Count - 3} more symbol(s) — run `suggest_doc_update on {action.RepoPath} {action.FilePath}` for the full output._");

        sb.AppendLine($"_Apply with: `write_back on {action.RepoPath} {action.FilePath}`_");
        return sb.ToString().TrimEnd();
    }

    private static async Task PostToSlackAsync(string responseUrl, string text)
    {
        try
        {
            using var http    = new HttpClient();
            var payload       = JsonSerializer.Serialize(new { text, response_type = "in_channel" });
            var content       = new StringContent(payload, Encoding.UTF8, "application/json");
            await http.PostAsync(responseUrl, content);
        }
        catch { /* best-effort */ }
    }

    // ── Slack signature validation ─────────────────────────────────────────

    private static bool ValidateSlackSignature(HttpRequest request, byte[] body)
    {
        var secret = Environment.GetEnvironmentVariable("SLACK_SIGNING_SECRET");
        if (string.IsNullOrEmpty(secret)) return false;

        var timestamp = request.Headers["X-Slack-Request-Timestamp"].ToString();
        var signature = request.Headers["X-Slack-Signature"].ToString();

        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;

        // Replay attack protection: reject requests older than 5 minutes
        if (long.TryParse(timestamp, out var ts))
        {
            var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts);
            if (age > 300) return false;
        }

        var baseString = $"v0:{timestamp}:{Encoding.UTF8.GetString(body)}";
        var expected   = "v0=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
                                Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }

    private static string? ExtractPayloadField(string form)
    {
        // form-encoded: "payload=%7B..."
        const string prefix = "payload=";
        var idx = form.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        return form[(idx + prefix.Length)..];
    }
}
