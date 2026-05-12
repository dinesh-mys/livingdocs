using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IStaleDocDetectorService
{
    Task<ScanResult> DetectAsync(string repoPath);
}
