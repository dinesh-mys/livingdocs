using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IDepartureRiskService
{
    Task<DepartureRiskReport> AnalyseAsync(string repoPath);
}
