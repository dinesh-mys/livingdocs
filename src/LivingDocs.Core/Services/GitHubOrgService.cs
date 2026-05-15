using System.Net.Http.Headers;
using System.Text.Json;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

/// <summary>Lists repositories in a GitHub organisation via the GitHub REST API. Uses GITHUB_TOKEN if set for private repo access and higher rate limits.</summary>
public class GitHubOrgService : IGitHubOrgService
{
    private readonly HttpClient _http;

    public GitHubOrgService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("User-Agent", "LivingDocs-OrgScanner");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<GitHubRepo>> ListReposAsync(string orgName, int maxCount = 30)
    {
        var result  = new List<GitHubRepo>();
        var page    = 1;
        var perPage = Math.Min(maxCount, 100);

        while (result.Count < maxCount)
        {
            var url  = $"https://api.github.com/orgs/{orgName}/repos?per_page={perPage}&page={page}&sort=updated&type=all";
            var json = await _http.GetStringAsync(url);
            var docs = JsonDocument.Parse(json).RootElement;

            if (docs.GetArrayLength() == 0) break;

            foreach (var repo in docs.EnumerateArray())
            {
                if (result.Count >= maxCount) break;

                var name     = repo.GetProperty("name").GetString()!;
                var cloneUrl = repo.GetProperty("clone_url").GetString()!;
                var branch   = repo.TryGetProperty("default_branch", out var b)
                               ? b.GetString()! : "main";
                var isPrivate = repo.GetProperty("private").GetBoolean();

                result.Add(new GitHubRepo(name, cloneUrl, branch, isPrivate));
            }

            page++;
        }

        return result;
    }
}
