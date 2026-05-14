using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IGitScannerService
{
    Task<IEnumerable<ChangeEvent>> ScanAsync(string repoPath, string? sinceCommit = null);

    /// <summary>Returns up to contextLines lines of source surrounding the given symbol name in filePath, so Claude can see current code state (not just the diff).</summary>
    Task<string?> GetSymbolContextAsync(string repoPath, string filePath, string symbolName, int contextLines = 30);
}
