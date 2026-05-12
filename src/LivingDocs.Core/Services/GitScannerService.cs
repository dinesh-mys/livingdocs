using System.Diagnostics;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class GitScannerService : IGitScannerService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go"
    };

    public async Task<IEnumerable<ChangeEvent>> ScanAsync(string repoPath, string? sinceCommit = null)
    {
        if (!IsGitRepo(repoPath))
            return Enumerable.Empty<ChangeEvent>();

        var commits = await GetCommitsAsync(repoPath, sinceCommit);
        var results = new List<ChangeEvent>();

        foreach (var commit in commits)
        {
            var changedFiles = await GetChangedFilesAsync(repoPath, commit.hash);
            foreach (var (filePath, changeType) in changedFiles)
            {
                if (!IsSupportedFile(filePath)) continue;

                var diff = await GetFileDiffAsync(repoPath, commit.hash, filePath);
                results.Add(new ChangeEvent
                {
                    CommitHash = commit.hash,
                    FilePath   = filePath,
                    Diff       = diff,
                    ChangedBy  = commit.author,
                    Timestamp  = commit.date,
                    ChangeType = changeType
                });
            }
        }

        return results;
    }

    private static bool IsGitRepo(string repoPath)
    {
        return Directory.Exists(Path.Combine(repoPath, ".git"));
    }

    private static bool IsSupportedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(ext);
    }

    private async Task<IEnumerable<(string hash, string author, DateTime date)>> GetCommitsAsync(
        string repoPath, string? sinceCommit)
    {
        // Format: hash|author|ISO date
        var args = sinceCommit != null
            ? $"log {sinceCommit}..HEAD --format=%H|%an|%aI"
            : "log -20 --format=%H|%an|%aI";

        var output = await RunGitAsync(repoPath, args);
        var commits = new List<(string, string, DateTime)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 3) continue;
            if (DateTime.TryParse(parts[2], out var date))
                commits.Add((parts[0].Trim(), parts[1].Trim(), date));
        }

        return commits;
    }

    private async Task<IEnumerable<(string filePath, ChangeType changeType)>> GetChangedFilesAsync(
        string repoPath, string commitHash)
    {
        // Format: M\tpath or A\tpath or D\tpath
        // --root ensures initial commits (no parent) are diffed against the empty tree
        var output = await RunGitAsync(repoPath, $"diff-tree --no-commit-id --root -r --name-status {commitHash}");
        var files = new List<(string, ChangeType)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;

            var changeType = parts[0].Trim() switch
            {
                "A" => ChangeType.Added,
                "D" => ChangeType.Deleted,
                _   => ChangeType.Modified
            };

            files.Add((parts[1].Trim(), changeType));
        }

        return files;
    }

    private async Task<string> GetFileDiffAsync(string repoPath, string commitHash, string filePath)
    {
        return await RunGitAsync(repoPath, $"show {commitHash} -- \"{filePath}\"");
    }

    internal static async Task<string> RunGitAsync(string repoPath, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory       = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
