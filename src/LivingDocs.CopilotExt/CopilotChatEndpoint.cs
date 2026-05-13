using System.Text.Json;
using System.Text.RegularExpressions;
using LivingDocs.Core.Interfaces;

/// <summary>GitHub Copilot Extension chat endpoint. Routes incoming messages to three handlers: "what changed" queries fetch a GitHub file diff via GitHubDiffService; "stale/scan" queries redirect the user to the scan_repo MCP tool; all other queries run semantic search over the local repo index (LIVINGDOCS_REPO_PATH) and answer via Claude.</summary>
public static class CopilotChatEndpoint
{
    public static async Task HandleAsync(
        HttpContext ctx,
        GitHubDiffService github,
        ISemanticSearchServiceFactory searchFactory,
        IClaudeService claude)
    {
        var token       = ctx.Request.Headers["X-GitHub-Token"].ToString();
        var body        = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var userMessage = ExtractLastUserMessage(body);

        ctx.Response.ContentType                   = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]      = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"]  = "no";

        await RouteAsync(ctx.Response.Body, userMessage, token, github, searchFactory, claude);
    }

    // ── Router ────────────────────────────────────────────────────────────

    private static async Task RouteAsync(
        Stream output, string message, string token,
        GitHubDiffService github,
        ISemanticSearchServiceFactory searchFactory,
        IClaudeService claude)
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

        // Any other question — try semantic search over the local repo index.
        await HandleQueryAsync(output, message, searchFactory, claude);
    }

    // ── What changed ─────────────────────────────────────────────────────

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

    // ── Semantic query ────────────────────────────────────────────────────

    private static async Task HandleQueryAsync(
        Stream output, string question,
        ISemanticSearchServiceFactory searchFactory,
        IClaudeService claude)
    {
        var repoPath = Environment.GetEnvironmentVariable("LIVINGDOCS_REPO_PATH");

        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            await SseWriter.WriteTextAsync(output,
                "**LivingDocs** can help with:\n\n" +
                "- `what changed in src/Tax.cs in owner/repo?` — summarize recent file changes\n" +
                "- `are there any stale docs?` — detect outdated documentation\n" +
                "- Natural-language questions about your codebase\n\n" +
                "To enable doc Q&A, set `LIVINGDOCS_REPO_PATH` to your local repository path " +
                "and run `index_repo` to build the search index.");
            return;
        }

        await using var search = searchFactory.Create(repoPath);
        var stats = search.GetStats();

        if (stats.TotalChunks == 0)
        {
            await SseWriter.WriteTextAsync(output,
                $"No search index found for `{repoPath}`.\n\n" +
                "Run `index_repo` via the MCP tool or CLI to build it:\n\n" +
                $"```\nlivingdocs-mcp index {repoPath}\n```");
            return;
        }

        var results = await search.SearchAsync(question, topK: 10);

        if (results.Count == 0)
        {
            await SseWriter.WriteTextAsync(output,
                "No relevant documentation found for that question. " +
                "Try rephrasing, or run `index_repo` to refresh the index.");
            return;
        }

        var answer = await claude.QueryDocsAsync(question, results.Select(r => r.Chunk));
        await SseWriter.WriteTextAsync(output, answer);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (string? file, string? repo) ExtractFileAndRepo(string message)
    {
        var full = Regex.Match(message,
            @"changed in\s+([\w./\\-]+\.\w+)\s+in\s+([\w-]+/[\w.-]+)",
            RegexOptions.IgnoreCase);
        if (full.Success)
            return (full.Groups[1].Value, full.Groups[2].Value);

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
