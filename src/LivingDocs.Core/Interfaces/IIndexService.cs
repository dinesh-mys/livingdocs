namespace LivingDocs.Core.Interfaces;

public interface IIndexService
{
    Task<int> IndexRepoAsync(string repoPath);
    Task ReIndexFileAsync(string repoPath, string filePath);
}
