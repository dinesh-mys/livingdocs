using System.Text;
using System.Text.Json;
using LivingDocs.Core.Models;

/// <summary>
/// Sends stale-doc alerts to a Slack channel via an incoming webhook URL.
/// Set SLACK_WEBHOOK_URL in the environment to enable. No-ops silently if unset.
/// </summary>
public static class SlackNotifier
{
    public static async Task NotifyStaleDocsAsync(
        string prUrl,
        string prTitle,
        int prNumber,
        string repoFullName,
        IReadOnlyList<StaleDoc> staleDocs,
        string impactSummary = "",
        IReadOnlyList<AffectedDoc>? affectedDocs = null)
    {
        var webhookUrl = Environment.GetEnvironmentVariable("SLACK_WEBHOOK_URL");
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        if (staleDocs.Count == 0) return;

        var payload = BuildPayload(prUrl, prTitle, prNumber, repoFullName, staleDocs, impactSummary, affectedDocs ?? []);
        try
        {
            using var http    = new HttpClient();
            var       content = new StringContent(payload, Encoding.UTF8, "application/json");
            await http.PostAsync(webhookUrl, content);
        }
        catch { /* best-effort — never crash the webhook handler */ }
    }

    private static string BuildPayload(
        string prUrl,
        string prTitle,
        int prNumber,
        string repoFullName,
        IReadOnlyList<StaleDoc> staleDocs,
        string impactSummary,
        IReadOnlyList<AffectedDoc> affectedDocs)
    {
        var fileList = new StringBuilder();
        foreach (var doc in staleDocs.Take(8))
        {
            var pct = (int)(doc.StaleScore * 100);
            fileList.AppendLine($"• `{doc.FilePath}` — {pct}% stale");
        }
        if (staleDocs.Count > 8)
            fileList.AppendLine($"_...and {staleDocs.Count - 8} more_");

        var blockList = new List<object>
        {
            new
            {
                type = "header",
                text = new { type = "plain_text", text = "📄 LivingDocs — Stale docs detected", emoji = true }
            },
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*<{prUrl}|PR #{prNumber}: {EscapeSlack(prTitle)}>* merged in `{repoFullName}`\n" +
                           $"*{staleDocs.Count}* file(s) changed in this PR may have outdated documentation."
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(impactSummary))
        {
            blockList.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"*What changed:* {EscapeSlack(impactSummary.Trim())}" }
            });
        }

        if (affectedDocs.Count > 0)
        {
            var docLines = new StringBuilder();
            foreach (var doc in affectedDocs.Take(5))
                docLines.AppendLine($"• `{doc.FilePath}` — {EscapeSlack(doc.Reason)}");

            blockList.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"*📚 Related docs to review:*\n{docLines.ToString().TrimEnd()}" }
            });
        }

        blockList.Add(new
        {
            type = "section",
            text = new { type = "mrkdwn", text = fileList.ToString().TrimEnd() }
        });

        blockList.Add(new
        {
            type = "actions",
            elements = new object[]
            {
                new
                {
                    type = "button",
                    text = new { type = "plain_text", text = "View PR", emoji = true },
                    url  = prUrl,
                    style = "primary"
                }
            }
        });

        blockList.Add(new
        {
            type = "context",
            elements = new object[]
            {
                new { type = "mrkdwn", text = "Run `suggest_doc_update` in Claude to auto-draft fixes · _LivingDocs_" }
            }
        });

        var blocks = blockList.ToArray();

        return JsonSerializer.Serialize(new { blocks });
    }

    private static string EscapeSlack(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
