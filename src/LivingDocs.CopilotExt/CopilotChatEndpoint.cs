using System.Text.Json;
using System.Text.RegularExpressions;

public static class CopilotChatEndpoint
{
    public static async Task HandleAsync(HttpContext ctx, GitHubDiffService github)
    {
        var token = ctx.Request.Headers["X-GitHub-Token"].ToString();
        var body   = await new StreamReader(ctx.Request.Body).ReadToEndAsync();

        var userMessage = ExtractLastUserMessage(body);

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        await RouteAsync(ctx.Response.Body, userMessage, token, github);
    }

    private static async Task RouteAsync(
        Stream output, string message, string token, GitHubDiffService github)
    {
        var lower = message.ToLowerInvariant();

        if (lower.Contains("what changed") || lower.Contains("changed in"))
        {
            await HandleWhatChangedAsync(output, message, token, github);
            return;
        }

        if (lower.Contains("stale") || lower.Contains("scan"))
        {
            await SseWriter.WriteTextAsync(output,
                "To scan for stale docs, use the `scan_repo` MCP tool:\n\n" +
                "```\nscan_repo on /path/to/repo\n```\n\n" +
                "Or run: `livingdocs-mcp scan /path/to/repo`");
            return;
        }

        await SseWriter.WriteTextAsync(output,
            "**LivingDocs** can help with:\n\n" +
            "- `what changed in src/Tax.cs in owner/repo?` — summarize recent changes to a file\n" +
            "- `are there any stale docs?` — detect outdated documentation\n\n" +
            "Example: `@livingdocs what changed in src/Auth.cs in dinesh-mys/livingdocs?`");
    }

    private static async Task HandleWhatChangedAsync(
        Stream output, string message, string token, GitHubDiffService github)
    {
        var (file, repo) = ExtractFileAndRepo(message);

        if (file is null)
        {
            await SseWriter.WriteTextAsync(output,
                "Please specify a file path, e.g.:\n\n" +
                "`@livingdocs what changed in src/Tax.cs in owner/repo?`");
            return;
        }

        if (repo is null)
        {
            await SseWriter.WriteTextAsync(output,
                $"Which repository is `{file}` in? Try:\n\n" +
                $"`@livingdocs what changed in {file} in owner/repo?`");
            return;
        }

        await SseWriter.WriteTextAsync(output,
            $"Fetching recent changes to `{file}` in `{repo}`...\n\n" +
            await github.GetRecentChangesAsync(repo, file, token));
    }

    private static (string? file, string? repo) ExtractFileAndRepo(string message)
    {
        // "what changed in src/Tax.cs in owner/repo"
        var full = Regex.Match(message,
            @"changed in\s+([\w./\\-]+\.\w+)\s+in\s+([\w-]+/[\w.-]+)",
            RegexOptions.IgnoreCase);
        if (full.Success)
            return (full.Groups[1].Value, full.Groups[2].Value);

        // "what changed in src/Tax.cs" (no repo)
        var fileOnly = Regex.Match(message,
            @"changed in\s+([\w./\\-]+\.\w+)",
            RegexOptions.IgnoreCase);
        if (fileOnly.Success)
            return (fileOnly.Groups[1].Value, null);

        return (null, null);
    }

    private static string ExtractLastUserMessage(string body)
    {
        try
        {
            var doc      = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages");
            var last     = string.Empty;
            foreach (var msg in messages.EnumerateArray())
            {
                if (msg.GetProperty("role").GetString() == "user")
                    last = msg.GetProperty("content").GetString() ?? string.Empty;
            }
            return last;
        }
        catch { return string.Empty; }
    }
}
