using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

/// <summary>Detects stale documentation by comparing when each doc comment was last committed (via git blame) against when the surrounding code last changed. Staleness is scored 0–1: 0 means fresh (code changed within 7 days of the doc), 1 means severely stale (90+ days gap).</summary>
public class StaleDocDetectorService : IStaleDocDetectorService
{
    private readonly IGitScannerService _scanner;
    private readonly IDocExtractorService _extractor;

    public StaleDocDetectorService(IGitScannerService scanner, IDocExtractorService extractor)
    {
        _scanner = scanner;
        _extractor = extractor;
    }

    public async Task<ScanResult> DetectAsync(string repoPath)
    {
        var changes = (await _scanner.ScanAsync(repoPath)).ToList();

        // Keep most-recent change per file
        var latestByFile = changes
            .GroupBy(c => c.FilePath)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Timestamp).First());

        var staleDocs   = new List<StaleDoc>();
        var filesScanned = new HashSet<string>();

        foreach (var (filePath, change) in latestByFile)
        {
            var fullPath = Path.Combine(repoPath, filePath);
            var content  = await ReadFileAsync(fullPath);
            if (string.IsNullOrEmpty(content)) continue;

            filesScanned.Add(filePath);

            var chunks = (await _extractor.ExtractAsync(filePath, content)).ToList();

            foreach (var chunk in chunks)
            {
                var docLastUpdated = await GetDocLastUpdatedAsync(repoPath, filePath, chunk.LineNumber);
                var score          = ComputeStaleScore(change.Timestamp, docLastUpdated);

                if (score > 0f)
                {
                    staleDocs.Add(new StaleDoc
                    {
                        FilePath        = filePath,
                        DocLastUpdated  = docLastUpdated,
                        CodeLastChanged = change.Timestamp,
                        StaleScore      = score
                    });
                }
            }
        }

        return new ScanResult
        {
            RepoPath   = repoPath,
            ScannedAt  = DateTime.UtcNow,
            StaleDocs  = staleDocs,
            TotalFiles = filesScanned.Count
        };
    }

    /// <summary>Computes a staleness score between 0 and 1. Returns 0 if code changed within 7 days of the doc update (considered fresh). Returns 1 if the gap is 90 or more days. Scales linearly between those bounds.</summary>
    internal static float ComputeStaleScore(DateTime codeChanged, DateTime docUpdated)
    {
        var days = (codeChanged - docUpdated).TotalDays;
        if (days <= 7)  return 0f;
        if (days >= 90) return 1f;
        return (float)((days - 7) / (90.0 - 7.0));
    }

    protected virtual Task<string> ReadFileAsync(string fullPath)
        => File.Exists(fullPath)
            ? File.ReadAllTextAsync(fullPath)
            : Task.FromResult(string.Empty);

    // Uses git blame to find when the doc-comment line was last committed.
    protected virtual async Task<DateTime> GetDocLastUpdatedAsync(
        string repoPath, string filePath, int lineNumber)
    {
        var args   = $"blame -L {lineNumber},{lineNumber} --porcelain -- \"{filePath}\"";
        var output = await GitScannerService.RunGitAsync(repoPath, args);

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("committer-time ") &&
                long.TryParse(line["committer-time ".Length..].Trim(), out var epoch))
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            }
        }

        return DateTime.MinValue;
    }
}
