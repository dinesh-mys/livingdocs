using System.ComponentModel;
using System.Text;
using LivingDocs.Core.Interfaces;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ProTools
{
    [McpServerTool(Name = "sync_confluence")]
    [Description(
        "PRO — Detect stale doc comments in a file, generate updated documentation via Claude, " +
        "and write the results to the matching Confluence page (creates the page if it doesn't exist). " +
        "Requires LIVINGDOCS_LICENSE_KEY, CONFLUENCE_BASE_URL, CONFLUENCE_EMAIL, CONFLUENCE_API_TOKEN, " +
        "and CONFLUENCE_SPACE_KEY to be set.")]
    public static async Task<string> SyncConfluence(
        ILicenseService          license,
        IStaleDocDetectorService detector,
        IDocExtractorService     extractor,
        IGitScannerService       scanner,
        IClaudeService           claude,
        IConfluenceService       confluence,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath,
        [Description("Confluence space key, e.g. DEV (overrides CONFLUENCE_SPACE_KEY env var)")] string? spaceKey = null)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license);
        if (licenseError is not null) return licenseError;

        var space = spaceKey
                 ?? Environment.GetEnvironmentVariable("CONFLUENCE_SPACE_KEY")
                 ?? string.Empty;

        if (string.IsNullOrWhiteSpace(space))
            return "Error: CONFLUENCE_SPACE_KEY is not set. Pass it as a parameter or set the environment variable.";

        var baseUrl = Environment.GetEnvironmentVariable("CONFLUENCE_BASE_URL") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "Error: CONFLUENCE_BASE_URL is not set (e.g. https://mycompany.atlassian.net/wiki).";

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONFLUENCE_API_TOKEN")))
            return "Error: CONFLUENCE_API_TOKEN is not set. Generate one at id.atlassian.com/manage-profile/security/api-tokens.";

        // ── 1. Find stale chunks in this file ─────────────────────────────────
        var fullPath = Path.Combine(repoPath, filePath);
        if (!File.Exists(fullPath))
            return $"Error: file not found: {fullPath}";

        var content = await File.ReadAllTextAsync(fullPath);
        var chunks  = (await extractor.ExtractAsync(filePath, content)).ToList();

        if (chunks.Count == 0)
            return $"No doc comments found in {filePath}.";

        var scanResult = await detector.DetectAsync(repoPath);
        var staleFiles = scanResult.StaleDocs.Select(d => d.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isFileStale = staleFiles.Contains(filePath);

        // ── 2. Get Claude suggestions for each chunk ──────────────────────────
        var changes = (await scanner.ScanAsync(repoPath)).ToList();

        var sb      = new StringBuilder();
        var updated = 0;

        sb.AppendLine($"<h2>Documentation — <code>{filePath}</code></h2>");
        sb.AppendLine($"<p><em>Synced by <a href=\"https://github.com/dinesh-mys/livingdocs\">LivingDocs</a> on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC</em></p>");

        if (!isFileStale)
        {
            sb.AppendLine("<p>✅ All documentation looks fresh — no updates needed.</p>");
        }
        else
        {
            foreach (var chunk in chunks)
            {
                var change = changes.FirstOrDefault(c =>
                    string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

                if (change is null) continue;

                var symbolContext = await scanner.GetSymbolContextAsync(
                    repoPath, filePath, chunk.ParentSymbol ?? string.Empty);

                var suggestion = await claude.SuggestDocUpdateAsync(change, chunk, symbolContext);

                var confidence  = $"{suggestion.Confidence:P0}";
                var reviewFlag  = suggestion.NeedsReview ? "⚠️ Low confidence — review recommended" : "✅";
                var symbolTitle = chunk.ParentSymbol ?? Path.GetFileNameWithoutExtension(filePath);

                sb.AppendLine($"<h3><code>{symbolTitle}</code></h3>");
                sb.AppendLine($"<p>{reviewFlag} Confidence: {confidence}</p>");
                sb.AppendLine("<h4>Updated documentation</h4>");
                sb.AppendLine($"<pre>{EscapeHtml(suggestion.Suggestion)}</pre>");
                sb.AppendLine("<hr />");
                updated++;
            }
        }

        if (updated == 0 && isFileStale)
            return $"No changes matched in {filePath} — try running 'scan_repo' first to refresh staleness data.";

        // ── 3. Upsert Confluence page ──────────────────────────────────────────
        var pageTitle = $"[LivingDocs] {Path.GetFileNameWithoutExtension(filePath)}";

        try
        {
            var pageUrl = await confluence.UpsertPageAsync(space, pageTitle, sb.ToString());
            return updated > 0
                ? $"✅ Synced {updated} doc comment(s) to Confluence: {pageUrl}"
                : $"✅ Confluence page updated (no stale docs): {pageUrl}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error syncing to Confluence: {ex.Message}\n" +
                   "Check CONFLUENCE_BASE_URL, CONFLUENCE_EMAIL, and CONFLUENCE_API_TOKEN.";
        }
    }

    [McpServerTool(Name = "scan_org")]
    [Description(
        "PRO — Scan every repository in a GitHub organisation and return a staleness report " +
        "across all repos. Requires LIVINGDOCS_LICENSE_KEY to be set.")]
    public static async Task<string> ScanOrg(
        ILicenseService license,
        [Description("GitHub organisation name, e.g. my-company")] string orgName)
    {
        var error = await LicenseGuard.RequireProAsync(license);
        if (error is not null) return error;

        // TODO: implement org-wide scan
        // 1. List repos via GitHub API (GITHUB_TOKEN env var)
        // 2. Clone / fetch each repo
        // 3. Run StaleDocDetectorService on each
        // 4. Return aggregated staleness report
        return $"[scan_org] Coming soon — will scan all repos in '{orgName}'.";
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
