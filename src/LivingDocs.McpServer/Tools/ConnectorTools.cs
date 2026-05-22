using System.ComponentModel;
using System.Text;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ConnectorTools
{
    // ── Slack ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "index_slack")]
    [Description(
        "PRO — Fetch messages from a Slack channel and add them to this repository's knowledge index. " +
        "Once indexed, query_docs will return results from Slack discussions alongside code documentation. " +
        "Threads are grouped so context is preserved. " +
        "Requires LIVINGDOCS_LICENSE_KEY and SLACK_BOT_TOKEN. " +
        "Bot needs channels:history and channels:read OAuth scopes.")]
    public static async Task<string> IndexSlack(
        ILicenseService license,
        ISlackConnectorService slack,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository — determines which index to write to")] string repoPath,
        [Description("Slack channel ID (e.g. C1234ABCD) or name prefixed with #")] string channelId,
        [Description("Maximum number of messages to fetch (default 200)")] int limit = 200)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license);
        if (licenseError is not null) return licenseError;

        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        IReadOnlyList<DocChunk> chunks;
        try
        {
            chunks = await slack.FetchChunksAsync(channelId, limit);
        }
        catch (Exception ex)
        {
            return $"Slack error: {ex.Message}\n\nEnsure SLACK_BOT_TOKEN is set and the bot is in the channel.";
        }

        if (chunks.Count == 0)
            return $"No meaningful messages found in channel '{channelId}' (messages under 30 chars and bot messages are skipped).";

        await using var search = searchFactory.Create(repoPath);
        var virtualPath = $"slack://{channelId}";

        await search.DeleteAsync(virtualPath);
        foreach (var chunk in chunks)
            await search.IndexAsync(chunk);
        await search.FlushAsync();

        var stats = search.GetStats();
        var sb    = new StringBuilder();
        sb.AppendLine($"Indexed {chunks.Count} Slack thread(s) from '{channelId}' into '{repoPath}'.");
        sb.AppendLine($"Total index size: {stats.TotalChunks} chunks (code docs + Slack).");
        sb.AppendLine();
        sb.AppendLine("Use query_docs to search across code and Slack:");
        sb.AppendLine($"  query_docs on {repoPath} \"how does the auth flow work?\"");
        return sb.ToString().TrimEnd();
    }

    // ── Microsoft Teams ───────────────────────────────────────────────────

    [McpServerTool(Name = "index_teams")]
    [Description(
        "PRO — Fetch messages from a Microsoft Teams channel via the Graph API and add them to this " +
        "repository's knowledge index. Indexed messages are searchable alongside code documentation via query_docs. " +
        "Requires LIVINGDOCS_LICENSE_KEY, TEAMS_TENANT_ID, TEAMS_CLIENT_ID, TEAMS_CLIENT_SECRET. " +
        "The Azure AD app needs ChannelMessage.Read.All application permission with admin consent.")]
    public static async Task<string> IndexTeams(
        ILicenseService license,
        ITeamsConnectorService teams,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository")] string repoPath,
        [Description("Microsoft Teams team ID (from Teams admin or Graph Explorer)")] string teamId,
        [Description("Teams channel ID (from Teams admin or Graph Explorer)")] string channelId,
        [Description("Maximum number of messages to fetch per request (max 50, default 50)")] int limit = 50)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license);
        if (licenseError is not null) return licenseError;

        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        IReadOnlyList<DocChunk> chunks;
        try
        {
            chunks = await teams.FetchChunksAsync(teamId, channelId, limit);
        }
        catch (Exception ex)
        {
            return BuildTeamsError(ex);
        }

        if (chunks.Count == 0)
            return $"No messages found in team '{teamId}' / channel '{channelId}' (messages under 30 chars are skipped).";

        await using var search = searchFactory.Create(repoPath);
        var virtualPath = $"teams://{teamId}/{channelId}";

        await search.DeleteAsync(virtualPath);
        foreach (var chunk in chunks)
            await search.IndexAsync(chunk);
        await search.FlushAsync();

        var stats = search.GetStats();
        var sb    = new StringBuilder();
        sb.AppendLine($"Indexed {chunks.Count} Teams message(s) from channel '{channelId}' into '{repoPath}'.");
        sb.AppendLine($"Total index size: {stats.TotalChunks} chunks (code docs + Teams).");
        sb.AppendLine();
        sb.AppendLine("Use query_docs to search across code and Teams:");
        sb.AppendLine($"  query_docs on {repoPath} \"what was decided about the API design?\"");
        return sb.ToString().TrimEnd();
    }

    // ── Email ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "index_email")]
    [Description(
        "PRO — Fetch emails from an IMAP mailbox folder and add them to this repository's knowledge index. " +
        "Engineering discussions, incident reports, and architectural decisions in email become " +
        "searchable alongside code documentation. Email quoting (> lines) is stripped automatically. " +
        "Requires LIVINGDOCS_LICENSE_KEY, IMAP_HOST, IMAP_USERNAME, IMAP_PASSWORD. " +
        "Optional: IMAP_PORT (default 993). Works with Gmail app passwords, Outlook, and any IMAP provider.")]
    public static async Task<string> IndexEmail(
        ILicenseService license,
        IEmailConnectorService email,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository")] string repoPath,
        [Description("IMAP folder name — use a dedicated folder like 'Engineering' for best results (default INBOX)")] string folder = "INBOX",
        [Description("Maximum number of emails to fetch (default 100)")] int limit = 100)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license);
        if (licenseError is not null) return licenseError;

        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        IReadOnlyList<DocChunk> chunks;
        try
        {
            chunks = await email.FetchChunksAsync(folder, limit);
        }
        catch (Exception ex)
        {
            return $"Email error: {ex.Message}\n\n" +
                   "Check IMAP_HOST, IMAP_USERNAME, IMAP_PASSWORD.\n" +
                   "For Gmail, use an App Password (not your account password):\n" +
                   "  myaccount.google.com → Security → 2-Step Verification → App Passwords";
        }

        if (chunks.Count == 0)
            return $"No emails found in folder '{folder}' (emails under 40 chars of body text are skipped).";

        await using var search = searchFactory.Create(repoPath);
        var virtualPath = $"email://{folder.ToLowerInvariant().Replace(' ', '-')}";

        await search.DeleteAsync(virtualPath);
        foreach (var chunk in chunks)
            await search.IndexAsync(chunk);
        await search.FlushAsync();

        var stats = search.GetStats();
        var sb    = new StringBuilder();
        sb.AppendLine($"Indexed {chunks.Count} email(s) from '{folder}' into '{repoPath}'.");
        sb.AppendLine($"Total index size: {stats.TotalChunks} chunks (code docs + email).");
        sb.AppendLine();
        sb.AppendLine("Use query_docs to search across code and email:");
        sb.AppendLine($"  query_docs on {repoPath} \"why did we choose Postgres over MySQL?\"");
        return sb.ToString().TrimEnd();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string BuildTeamsError(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Teams error: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Setup checklist:");
        sb.AppendLine("  1. Register an app in Azure AD (portal.azure.com → App registrations)");
        sb.AppendLine("  2. Add application permission: ChannelMessage.Read.All");
        sb.AppendLine("  3. Grant admin consent");
        sb.AppendLine("  4. Create a client secret");
        sb.AppendLine("  5. Set TEAMS_TENANT_ID, TEAMS_CLIENT_ID, TEAMS_CLIENT_SECRET");
        sb.AppendLine("  6. Get team/channel IDs from Teams Admin or Graph Explorer:");
        sb.AppendLine("     https://developer.microsoft.com/graph/graph-explorer");
        return sb.ToString().TrimEnd();
    }
}
