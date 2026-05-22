using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class DepartureRiskService : IDepartureRiskService
{
    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go" };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".livingdocs", "dist", "coverage" };

    public async Task<DepartureRiskReport> AnalyseAsync(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return new DepartureRiskReport(repoPath, 0, [], []);

        var files = Directory
            .EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExt.Contains(Path.GetExtension(f)))
            .Where(f => !IsInSkippedDir(repoPath, f))
            .Where(f => !IsTestFile(f))
            .ToList();

        var riskyFiles = new List<FileRisk>();

        foreach (var fullPath in files)
        {
            var relPath   = Path.GetRelativePath(repoPath, fullPath);
            var ownership = await GetOwnershipAsync(repoPath, relPath);
            if (ownership.Count == 0) continue;

            var total    = ownership.Sum(o => o.CommitCount);
            var fileRisk = new FileRisk(relPath, total, ownership);

            if (fileRisk.IsSinglePointOfKnowledge)
                riskyFiles.Add(fileRisk);
        }

        var sorted = riskyFiles
            .OrderByDescending(f => f.TopAuthors[0].Percentage)
            .ThenByDescending(f => f.TotalCommits)
            .ToList();

        var authorSummaries = sorted
            .GroupBy(f => f.TopAuthors[0].Author)
            .Select(g => new AuthorRiskSummary(
                g.Key,
                g.Select(f => f.FilePath).ToList()))
            .OrderByDescending(a => a.OwnedFiles.Count)
            .ToList();

        return new DepartureRiskReport(repoPath, files.Count, sorted, authorSummaries);
    }

    private static async Task<IReadOnlyList<AuthorOwnership>> GetOwnershipAsync(
        string repoPath, string relPath)
    {
        try
        {
            // git shortlog -sn -- <file>  →  "    12\tAlice Chen"
            var output = await GitScannerService.RunGitAsync(
                repoPath, $"shortlog -sn HEAD -- \"{relPath}\"");

            var result = new List<AuthorOwnership>();
            int total  = 0;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var tab = line.IndexOf('\t');
                if (tab < 0) continue;
                if (!int.TryParse(line[..tab].Trim(), out var count)) continue;
                var author = line[(tab + 1)..].Trim();
                result.Add(new AuthorOwnership(author, count, 0));
                total += count;
            }

            if (total == 0) return [];

            return result
                .Select(o => o with { Percentage = (double)o.CommitCount / total })
                .OrderByDescending(o => o.CommitCount)
                .Take(5)
                .ToList();
        }
        catch { return []; }
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
}
