using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IGapDetectorService
{
    Task<GapReport> DetectAsync(string repoPath);
}
