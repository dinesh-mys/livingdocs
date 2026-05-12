using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IGitScannerService
{
    Task<IEnumerable<ChangeEvent>> ScanAsync(string repoPath, string? sinceCommit = null);
}
