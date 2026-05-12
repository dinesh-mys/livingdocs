using System.ComponentModel;
using System.Text;
using LivingDocs.Core.Interfaces;
using ModelContextProtocol.Server;

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

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "suggest_doc_update")]
    [Description(
        "Use Claude to suggest an updated documentation comment for a specific file, " +
        "based on its most recent code diff. Requires ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> SuggestDocUpdate(
        IClaudeService claude,
        IGitScannerService scanner,
        IDocExtractorService extractor,
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
        var sb           = new StringBuilder();

        foreach (var chunk in chunks)
        {
            var suggestion = await claude.SuggestDocUpdateAsync(latestChange, chunk);
            sb.AppendLine($"### {chunk.ParentSymbol ?? "unknown"}");
            sb.AppendLine(suggestion);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
