using System.ComponentModel;
using System.Text;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Services;
using ModelContextProtocol.Server;
using LivingDocs.Core.Models;

[McpServerToolType]
public static class LivingDocsTools
{
    [McpServerTool(Name = "scan_repo", ReadOnly = true)]
    [Description(
        "Scan a local git repository and return all source files where documentation " +
        "comments appear out of date with recent code changes. " +
        "Staleness is scored 0–100%: 0% means fresh, 100% means the doc hasn't been " +
        "touched in 90+ days since the code changed.")]
    public static async Task<string> ScanRepo(
        IStaleDocDetectorService detector,
        ILicenseService license,
        ITelemetryService telemetry,
        [Description("Absolute path to the local git repository to scan")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var result = await detector.DetectAsync(repoPath);

        if (result.StaleDocs.Count == 0)
            return $"Scanned {result.TotalFiles} file(s) in '{repoPath}'. All documentation looks fresh.";

        var sb = new StringBuilder();
        sb.AppendLine($"Scanned {result.TotalFiles} file(s) in '{repoPath}'.");
        sb.AppendLine($"Found {result.StaleDocs.Count} potentially stale doc(s):");
        sb.AppendLine();

        foreach (var doc in result.StaleDocs.OrderByDescending(d => d.StaleScore))
        {
            sb.AppendLine($"• {doc.FilePath}  (staleness: {doc.StaleScore:P0})");
            sb.AppendLine($"  Doc last updated : {doc.DocLastUpdated:yyyy-MM-dd}");
            sb.AppendLine($"  Code last changed: {doc.CodeLastChanged:yyyy-MM-dd}");
        }

        // Value-first trial offer: only for free users (not expired/invalid paid keys),
        // and only when there is value to act on (stale docs were found above).
        var status = await license.GetStatusAsync();
        if (!status.IsValid && status.Plan == "free")
        {
            telemetry.Track("upsell_shown", new Dictionary<string, string> { ["source"] = "scan" });
            sb.AppendLine();
            sb.AppendLine($"💡 Found {result.StaleDocs.Count} stale doc(s). `write_back` can fix them");
            sb.AppendLine("   automatically, in place — free for 7 days, no card needed →");
            sb.AppendLine("   https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "query_docs", ReadOnly = true)]
    [Description(
        "Answer a natural-language question about a codebase using semantic search over its " +
        "documentation comments. Run index_repo first if you haven't indexed this repository yet. " +
        "Requires ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> QueryDocs(
        IClaudeService claude,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository")] string repoPath,
        [Description("Natural-language question, e.g. 'What does the Tax class do?' or 'How is authentication handled?'")] string question)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        try
        {
            await using var search = searchFactory.Create(repoPath);

            var stats = search.GetStats();
            if (stats.TotalChunks == 0)
                return $"No search index found for '{repoPath}'.\n\n" +
                       $"Run `index_repo` first to build the semantic index:\n\n" +
                       $"```\nindex_repo on {repoPath}\n```";

            var results = await search.SearchAsync(question, topK: 10);
            if (results.Count == 0)
                return "No relevant documentation found for that question.";

            return await claude.QueryDocsAsync(question, results.Select(r => r.Chunk));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetType().Name} — {ex.Message}";
        }
    }

    [McpServerTool(Name = "index_repo")]
    [Description(
        "Build or refresh the semantic search index for a repository. " +
        "Run this once before using query_docs, then again after significant code changes. " +
        "The index is stored in a .livingdocs/ folder inside the repository. " +
        "Requires ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> IndexRepo(
        ITelemetryService telemetry,
        IIndexService indexer,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var total = await indexer.IndexRepoAsync(repoPath);

        if (total == 0)
            return $"No documentation comments found in '{repoPath}'. " +
                   $"Add doc comments (///, JSDoc, or docstrings) and re-run index_repo.";

        telemetry.Track("index_success", new Dictionary<string, string> { ["chunks"] = Bucket(total) });
        await using var search = searchFactory.Create(repoPath);
        var stats = search.GetStats();
        var since = stats.LastIndexed.HasValue
            ? $" (last indexed {stats.LastIndexed.Value:yyyy-MM-dd HH:mm} UTC)"
            : string.Empty;

        return $"Indexed {total} documentation chunk(s) in '{repoPath}'{since}.\n\n" +
               $"Semantic search is ready — use `query_docs` to ask questions about this codebase.";
    }

    [McpServerTool(Name = "suggest_doc_update")]
    [Description(
        "Use Claude to suggest an updated documentation comment for a specific file, " +
        "based on its most recent code diff. Requires ANTHROPIC_API_KEY to be set. " +
        "Set format='patch' to get a unified diff (--- / +++ / @@) ready for git apply.")]
    public static async Task<string> SuggestDocUpdate(
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath,
        [Description("Output format: 'text' (default) returns the suggested comment; 'patch' returns a unified diff ready for git apply")] string format = "text")
    {
        if (!Directory.Exists(repoPath))
            return $"Error: repository not found — {repoPath}";

        var asPatch = string.Equals(format, "patch", StringComparison.OrdinalIgnoreCase);

        var changes = (await scanner.ScanAsync(repoPath))
            .Where(c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .ToList();

        if (changes.Count == 0)
            return $"No recent changes found for '{filePath}'. Has this file been committed?";

        var fullPath = Path.Combine(repoPath, filePath);
        if (!File.Exists(fullPath))
            return $"Error: file not found — {filePath}";

        var content = await File.ReadAllTextAsync(fullPath);
        var chunks  = (await extractor.ExtractAsync(filePath, content)).ToList();

        if (chunks.Count == 0)
            return $"No documentation comments found in '{filePath}'.";

        var latestChange = changes[0];
        var sb           = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var symbolContext = chunk.ParentSymbol is not null
                ? await scanner.GetSymbolContextAsync(repoPath, filePath, chunk.ParentSymbol)
                : null;

            var result = await claude.SuggestDocUpdateAsync(latestChange, chunk, symbolContext);

            sb.AppendLine($"### {chunk.ParentSymbol ?? "unknown"}");

            if (result.NeedsReview)
                sb.AppendLine($"⚠️ LOW CONFIDENCE ({result.Confidence:P0}) — please review before applying");
            else
                sb.AppendLine($"✓ Confidence: {result.Confidence:P0}");

            sb.AppendLine();

            if (asPatch)
            {
                var patch = DocPatchFormatter.Format(chunk.Content, result.Suggestion, filePath, chunk.LineNumber);
                sb.AppendLine("```diff");
                sb.AppendLine(patch);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine(result.Suggestion);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "detect_gaps", ReadOnly = true)]
    [Description(
        "Find source files that have NO documentation comments (XML ///, JSDoc, or docstrings). " +
        "Results are ranked by commit activity so the busiest undocumented files appear first — " +
        "these are the highest-priority knowledge gaps. " +
        "Skips test files, build output folders, and node_modules.")]
    public static async Task<string> DetectGaps(
        IGapDetectorService gapDetector,
        [Description("Absolute path to the local git repository")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var report = await gapDetector.DetectAsync(repoPath);

        var undocumented = report.TotalFiles - report.DocumentedFiles;
        var pct = report.TotalFiles > 0
            ? (double)undocumented / report.TotalFiles * 100
            : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Knowledge gaps in '{repoPath}'");
        sb.AppendLine($"Files scanned: {report.TotalFiles}  |  Documented: {report.DocumentedFiles}  |  Undocumented: {undocumented} ({pct:F0}%)");

        if (report.Gaps.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No knowledge gaps found — every file has documentation.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        sb.AppendLine("Top undocumented files by activity:");
        sb.AppendLine();

        foreach (var gap in report.Gaps.Take(20))
        {
            var date   = gap.LastChanged == DateTime.MinValue ? "unknown" : gap.LastChanged.ToString("yyyy-MM-dd");
            var author = string.IsNullOrEmpty(gap.LastAuthor) ? "" : $"  by {gap.LastAuthor}";
            var commits = gap.CommitCount > 0 ? $"{gap.CommitCount} commit{(gap.CommitCount == 1 ? "" : "s")}" : "no history";
            sb.AppendLine($"  {gap.FilePath,-55} {commits,-12}  last changed {date}{author}");
        }

        if (report.Gaps.Count > 20)
            sb.AppendLine($"  ... and {report.Gaps.Count - 20} more");

        sb.AppendLine();
        sb.AppendLine("To add docs to the highest-priority file, run:");
        sb.AppendLine($"  suggest_doc_update on {repoPath} {report.Gaps[0].FilePath}");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "departure_risk", ReadOnly = true)]
    [Description(
        "Identify authors who are the sole or dominant contributor to critical source files — " +
        "the 'bus factor 1' problem. Files where one person accounts for ≥60% of commits AND " +
        "has at least 5 commits are flagged as single points of knowledge. " +
        "Results group by author so you can see at a glance who holds the most undocumented knowledge. " +
        "Run detect_gaps afterwards to find which of these files also lack documentation.")]
    public static async Task<string> DepartureRisk(
        IDepartureRiskService riskService,
        [Description("Absolute path to the local git repository")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var report = await riskService.AnalyseAsync(repoPath);

        var sb = new StringBuilder();
        sb.AppendLine($"Departure risk analysis for '{repoPath}'");
        sb.AppendLine($"Files analysed: {report.FilesAnalysed}  |  High-risk files: {report.RiskyFiles.Count}");

        if (report.RiskyFiles.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No single points of knowledge found.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        sb.AppendLine("Authors with exclusive knowledge:");
        sb.AppendLine();

        foreach (var author in report.AuthorSummaries)
        {
            sb.AppendLine($"  {author.Author}  ({author.OwnedFiles.Count} file{(author.OwnedFiles.Count == 1 ? "" : "s")})");
            foreach (var filePath in author.OwnedFiles.Take(10))
            {
                var risk = report.RiskyFiles.First(f => f.FilePath == filePath);
                var top  = risk.TopAuthors[0];
                sb.AppendLine($"    • {filePath,-55} {risk.TotalCommits} commits  ({top.Percentage:P0} by {top.Author})");
            }
            if (author.OwnedFiles.Count > 10)
                sb.AppendLine($"    ... and {author.OwnedFiles.Count - 10} more");
            sb.AppendLine();
        }

        sb.AppendLine("To generate a handover doc for the riskiest file:");
        sb.AppendLine($"  suggest_doc_update on {repoPath} {report.RiskyFiles[0].FilePath}");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "write_back")]
    [Description(
        "Generate an updated doc comment for every symbol in a file and write it directly " +
        "back to disk. Low-confidence suggestions (< 60%) are skipped and listed separately " +
        "for manual review. Requires ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> WriteBack(
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor,
        IDocWriterService writer,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: repository not found — {repoPath}";

        var changes = (await scanner.ScanAsync(repoPath))
            .Where(c => string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Timestamp)
            .ToList();

        if (changes.Count == 0)
            return $"No recent changes found for '{filePath}'. Has this file been committed?";

        var fullPath = Path.Combine(repoPath, filePath);
        if (!File.Exists(fullPath))
            return $"Error: file not found — {filePath}";

        var content = await File.ReadAllTextAsync(fullPath);
        var chunks  = (await extractor.ExtractAsync(filePath, content)).ToList();

        if (chunks.Count == 0)
            return $"No documentation comments found in '{filePath}'.";

        var latestChange = changes[0];
        var written      = new List<string>();
        var skipped      = new List<string>();

        // Process in reverse line order so earlier line numbers remain valid after each splice.
        foreach (var chunk in chunks.OrderByDescending(c => c.LineNumber))
        {
            var symbolContext = chunk.ParentSymbol is not null
                ? await scanner.GetSymbolContextAsync(repoPath, filePath, chunk.ParentSymbol)
                : null;

            var result = await claude.SuggestDocUpdateAsync(latestChange, chunk, symbolContext);

            if (result.NeedsReview)
            {
                skipped.Add($"• {chunk.ParentSymbol ?? "unknown"} (confidence {result.Confidence:P0})");
                continue;
            }

            await writer.WriteBackAsync(repoPath, filePath, chunk, result.Suggestion);
            written.Add($"• {chunk.ParentSymbol ?? "unknown"} (confidence {result.Confidence:P0})");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"write_back complete for `{filePath}`");
        sb.AppendLine();

        if (written.Count > 0)
        {
            sb.AppendLine($"**Written ({written.Count}):**");
            written.ForEach(l => sb.AppendLine(l));
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — low confidence ({skipped.Count}), review manually:**");
            skipped.ForEach(l => sb.AppendLine(l));
            sb.AppendLine();
            sb.AppendLine("Run `suggest_doc_update` on this file to review the suggestions before applying.");
        }

        return sb.ToString().TrimEnd();
    }

    // Coarse bucket so we never collect exact counts. Internal so the CLI index path reuses it.
    internal static string Bucket(int n) =>
        n switch
        {
            < 10  => "1-9",
            < 50  => "10-49",
            < 200 => "50-199",
            _     => "200+",
        };
}
