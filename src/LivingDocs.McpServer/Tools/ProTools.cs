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
        ITelemetryService        telemetry,
        IStaleDocDetectorService detector,
        IDocExtractorService     extractor,
        IGitScannerService       scanner,
        IClaudeService           claude,
        IConfluenceService       confluence,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath,
        [Description("Confluence space key, e.g. DEV (overrides CONFLUENCE_SPACE_KEY env var)")] string? spaceKey = null)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license, telemetry);
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

    [McpServerTool(Name = "write_docs")]
    [Description(
        "PRO — Detect stale doc comments in a file, generate updated documentation via Claude, " +
        "and append the results with a UTC timestamp to docs/<FileName>.md inside the repository. " +
        "Each run appends a new dated section so you get a full audit trail of doc history. " +
        "Requires LIVINGDOCS_LICENSE_KEY and ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> WriteDocs(
        ILicenseService          license,
        ITelemetryService        telemetry,
        IStaleDocDetectorService detector,
        IDocExtractorService     extractor,
        IGitScannerService       scanner,
        IClaudeService           claude,
        IDocWriterService        writer,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license, telemetry);
        if (licenseError is not null) return licenseError;

        var fullPath = Path.Combine(repoPath, filePath);
        if (!File.Exists(fullPath))
            return $"Error: file not found: {fullPath}";

        // ── 1. Extract doc chunks ─────────────────────────────────────────────
        var content = await File.ReadAllTextAsync(fullPath);
        var chunks  = (await extractor.ExtractAsync(filePath, content)).ToList();

        if (chunks.Count == 0)
            return $"No documentation comments found in {filePath}.";

        // ── 2. Get latest git change for the file ─────────────────────────────
        var changes = (await scanner.ScanAsync(repoPath))
            .Where(c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .ToList();

        if (changes.Count == 0)
            return $"No recent git changes found for '{filePath}'. Has this file been committed?";

        var latestChange = changes[0];

        // ── 3. Generate Claude suggestions for each chunk ─────────────────────
        var entries  = new List<(string Symbol, string Suggestion, float Confidence)>();
        var skipped  = new List<string>();

        foreach (var chunk in chunks)
        {
            var symbolContext = chunk.ParentSymbol is not null
                ? await scanner.GetSymbolContextAsync(repoPath, filePath, chunk.ParentSymbol)
                : null;

            var suggestion = await claude.SuggestDocUpdateAsync(latestChange, chunk, symbolContext);

            if (suggestion.NeedsReview)
            {
                skipped.Add($"• {chunk.ParentSymbol ?? "unknown"} (confidence {suggestion.Confidence:P0})");
                continue;
            }

            entries.Add((chunk.ParentSymbol ?? "unknown", suggestion.Suggestion, suggestion.Confidence));
        }

        if (entries.Count == 0)
            return $"All suggestions were low-confidence for '{filePath}'. Run `suggest_doc_update` to review manually.";

        // ── 4. Append to docs/<File>.md ───────────────────────────────────────
        var mdRelPath = await writer.WriteDocsAsync(repoPath, filePath, entries);

        var sb = new StringBuilder();
        sb.AppendLine($"✅ Wrote {entries.Count} doc section(s) to `{mdRelPath}`");
        sb.AppendLine();
        foreach (var (symbol, _, confidence) in entries)
            sb.AppendLine($"  • {symbol} ({confidence:P0})");

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ Skipped {skipped.Count} low-confidence section(s) — review with `suggest_doc_update`:");
            skipped.ForEach(l => sb.AppendLine(l));
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "scan_org")]
    [Description(
        "PRO — Scan every repository in a GitHub organisation and return an org-wide staleness " +
        "report. Clones each repo to a temp directory, runs stale-doc detection, then cleans up. " +
        "Requires LIVINGDOCS_LICENSE_KEY and GITHUB_TOKEN (for private repos + higher rate limits).")]
    public static async Task<string> ScanOrg(
        ILicenseService          license,
        ITelemetryService        telemetry,
        IStaleDocDetectorService detector,
        IGitHubOrgService        github,
        [Description("GitHub organisation or user name, e.g. my-company")] string orgName,
        [Description("Maximum number of repos to scan (default 20)")] int maxRepos = 20)
    {
        var licenseError = await LicenseGuard.RequireProAsync(license, telemetry);
        if (licenseError is not null) return licenseError;

        // ── 1. List repos ──────────────────────────────────────────────────────
        List<GitHubRepo> repos;
        try
        {
            repos = await github.ListReposAsync(orgName, maxRepos);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return $"Organisation '{orgName}' not found. Check the name and ensure GITHUB_TOKEN has org read access.";
        }

        if (repos.Count == 0)
            return $"No repositories found in '{orgName}'.";

        // ── 2. Clone each repo, scan, clean up ────────────────────────────────
        var tempRoot = Path.Combine(Path.GetTempPath(), $"livingdocs-org-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var results  = new List<(string Repo, int StaleCount, int TotalFiles, List<string> TopStale)>();
        var failed   = new List<string>();

        foreach (var repo in repos)
        {
            var repoDir = Path.Combine(tempRoot, repo.Name);
            try
            {
                // Shallow clone — fast, only needs recent history for staleness
                await RunGitCloneAsync(repo.CloneUrl, repoDir, repo.DefaultBranch);

                var scan      = await detector.DetectAsync(repoDir);
                var topStale  = scan.StaleDocs
                    .OrderByDescending(d => d.StaleScore)
                    .Take(3)
                    .Select(d => $"{d.FilePath} ({d.StaleScore:P0})")
                    .ToList();

                results.Add((repo.Name, scan.StaleDocs.Count, scan.TotalFiles, topStale));
            }
            catch
            {
                failed.Add(repo.Name);
            }
            finally
            {
                if (Directory.Exists(repoDir))
                    Directory.Delete(repoDir, recursive: true);
            }
        }

        Directory.Delete(tempRoot, recursive: true);

        // ── 3. Format report ──────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine($"## 🏢 LivingDocs — Org Scan: {orgName}");
        sb.AppendLine();
        sb.AppendLine($"Scanned **{results.Count}** repo(s) | " +
                      $"**{results.Sum(r => r.TotalFiles)}** files examined | " +
                      $"**{results.Sum(r => r.StaleCount)}** stale doc(s) found");
        sb.AppendLine();

        var staleRepos  = results.Where(r => r.StaleCount > 0)
                                 .OrderByDescending(r => r.StaleCount).ToList();
        var cleanRepos  = results.Where(r => r.StaleCount == 0).ToList();

        if (staleRepos.Count > 0)
        {
            sb.AppendLine("### ⚠️ Repos with stale docs");
            sb.AppendLine();
            foreach (var (repo, staleCount, totalFiles, topStale) in staleRepos)
            {
                sb.AppendLine($"**{repo}** — {staleCount} stale / {totalFiles} files");
                foreach (var s in topStale)
                    sb.AppendLine($"  - {s}");
                sb.AppendLine();
            }
        }

        if (cleanRepos.Count > 0)
        {
            sb.AppendLine($"### ✅ Clean repos ({cleanRepos.Count})");
            sb.AppendLine(string.Join(", ", cleanRepos.Select(r => r.Repo)));
            sb.AppendLine();
        }

        if (failed.Count > 0)
        {
            sb.AppendLine($"### ⚡ Skipped (clone failed)");
            sb.AppendLine(string.Join(", ", failed));
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task RunGitCloneAsync(string cloneUrl, string targetDir, string branch)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git",
            $"clone --depth=50 --single-branch --branch {branch} {cloneUrl} \"{targetDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"git clone failed: {err}");
        }
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
