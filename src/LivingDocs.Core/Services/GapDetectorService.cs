using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class GapDetectorService : IGapDetectorService
{
    private readonly IDocExtractorService _extractor;

    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go" };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".livingdocs", "dist", "coverage" };

    public GapDetectorService(IDocExtractorService extractor) => _extractor = extractor;

    public async Task<GapReport> DetectAsync(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return new GapReport(repoPath, 0, 0, []);

        var files = Directory
            .EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExt.Contains(Path.GetExtension(f)))
            .Where(f => !IsInSkippedDir(repoPath, f))
            .Where(f => !IsTestFile(f))
            .ToList();

        var total      = files.Count;
        var documented = 0;
        var gaps       = new List<DocGap>();

        foreach (var fullPath in files)
        {
            var relPath = Path.GetRelativePath(repoPath, fullPath);
            string content;
            try   { content = await File.ReadAllTextAsync(fullPath); }
            catch { continue; }

            var chunks = (await _extractor.ExtractAsync(relPath, content)).ToList();

            if (chunks.Count > 0)
            {
                documented++;
                continue;
            }

            var (commitCount, lastChanged, lastAuthor) = await GetFileGitInfoAsync(repoPath, relPath);
            gaps.Add(new DocGap(relPath, commitCount, lastChanged, lastAuthor));
        }

        var sorted = gaps
            .OrderByDescending(g => g.CommitCount)
            .ThenByDescending(g => g.LastChanged)
            .ToList();

        return new GapReport(repoPath, total, documented, sorted);
    }

    private static bool IsInSkippedDir(string repoPath, string fullPath)
    {
        var rel = Path.GetRelativePath(repoPath, fullPath);
        return rel.Split(Path.DirectorySeparatorChar).Any(seg => SkipDirs.Contains(seg));
    }

    private static bool IsTestFile(string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".spec", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_test", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int CommitCount, DateTime LastChanged, string LastAuthor)> GetFileGitInfoAsync(
        string repoPath, string relPath)
    {
        try
        {
            // Count commits and get latest commit date + author in one log call
            var log = await GitScannerService.RunGitAsync(
                repoPath, $"log --format=%aI|%an -- \"{relPath}\"");

            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length == 0)
                return (0, DateTime.MinValue, string.Empty);

            var parts = lines[0].Split('|', 2);
            var lastChanged = DateTime.TryParse(
                parts.Length > 0 ? parts[0].Trim() : string.Empty, out var dt)
                ? dt.ToUniversalTime()
                : DateTime.MinValue;
            var lastAuthor = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            return (lines.Length, lastChanged, lastAuthor);
        }
        catch
        {
            return (0, DateTime.MinValue, string.Empty);
        }
    }
}
