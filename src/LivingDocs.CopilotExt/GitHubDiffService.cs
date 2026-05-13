using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>Fetches recent commit history and unified diffs for a specific file in a GitHub repository using the GitHub REST API. Requires a valid GitHub user token passed via X-GitHub-Token.</summary>
public class GitHubDiffService(HttpClient http)
{
    public async Task<string> GetRecentChangesAsync(string repo, string filePath, string token)
    {
        if (string.IsNullOrEmpty(token))
            return "_GitHub token not available — cannot fetch changes._";

        try
        {
            http.DefaultRequestHeaders.Clear();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Add("User-Agent", "LivingDocs-Copilot-Extension");
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // Get the most recent commit that touched this file
            var commitsUrl = $"https://api.github.com/repos/{repo}/commits?path={Uri.EscapeDataString(filePath)}&per_page=3";
            var commitsJson = await http.GetStringAsync(commitsUrl);
            var commits = JsonDocument.Parse(commitsJson).RootElement;

            if (commits.GetArrayLength() == 0)
                return $"No recent commits found for `{filePath}` in `{repo}`.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"### Recent changes to `{filePath}` in `{repo}`\n");

            foreach (var commit in commits.EnumerateArray())
            {
                var sha     = commit.GetProperty("sha").GetString()![..7];
                var message = commit.GetProperty("commit").GetProperty("message").GetString() ?? "";
                var author  = commit.GetProperty("commit").GetProperty("author").GetProperty("name").GetString() ?? "";
                var date    = commit.GetProperty("commit").GetProperty("author").GetProperty("date").GetString() ?? "";

                sb.AppendLine($"- **{sha}** — {message.Split('\n')[0]}");
                sb.AppendLine($"  by {author} on {date[..10]}");
            }

            // Get the diff for the latest commit
            var latestSha = commits[0].GetProperty("sha").GetString()!;
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github.diff"));

            var diffUrl  = $"https://api.github.com/repos/{repo}/commits/{latestSha}";
            var diff     = await http.GetStringAsync(diffUrl);
            var fileDiff = ExtractFileDiff(diff, filePath);

            if (fileDiff is not null)
            {
                sb.AppendLine($"\n### Latest diff\n\n```diff\n{fileDiff}\n```");
            }

            return sb.ToString();
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            return $"Repository `{repo}` or file `{filePath}` not found. Check the repo name and file path.";
        }
        catch (Exception ex)
        {
            return $"Error fetching changes: {ex.Message}";
        }
    }

    private static string? ExtractFileDiff(string fullDiff, string filePath)
    {
        var lines  = fullDiff.Split('\n');
        var inFile = false;
        var result = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git") && line.Contains(filePath))
            {
                inFile = true;
            }
            else if (line.StartsWith("diff --git") && inFile)
            {
                break;
            }

            if (inFile) result.AppendLine(line);
        }

        var text = result.ToString().Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
