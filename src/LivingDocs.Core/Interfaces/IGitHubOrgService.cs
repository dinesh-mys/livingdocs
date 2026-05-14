namespace LivingDocs.Core.Interfaces;

public interface IGitHubOrgService
{
    /// <summary>Lists public and (with token) private repos in a GitHub org, up to maxCount.</summary>
    Task<List<GitHubRepo>> ListReposAsync(string orgName, int maxCount = 30);
}

public record GitHubRepo(string Name, string CloneUrl, string DefaultBranch, bool IsPrivate);
