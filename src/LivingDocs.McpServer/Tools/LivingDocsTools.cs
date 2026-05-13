using System.ComponentModel;
using System.Text;
using LivingDocs.Core.Interfaces;
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

    [McpServerTool(Name = "index_repo")]
    [Description(
        "Build or refresh the semantic search index for a repository. " +
        "Run this once before using query_docs, then again after significant code changes. " +
        "The index is stored in a .livingdocs/ folder inside the repository. " +
        "Requires ANTHROPIC_API_KEY to be set.")]
    public static async Task<string> IndexRepo(
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
