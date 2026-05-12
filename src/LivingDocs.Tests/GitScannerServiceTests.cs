using LivingDocs.Core.Models;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class GitScannerServiceTests : IDisposable
{
    private readonly string _repoPath;
    private readonly GitScannerService _sut = new();

    public GitScannerServiceTests()
    {
        // Create a real temp git repo for each test
        _repoPath = Path.Combine(Path.GetTempPath(), "livingdocs-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_repoPath);
        GitScannerService.RunGitAsync(_repoPath, "init").GetAwaiter().GetResult();
        GitScannerService.RunGitAsync(_repoPath, "config user.email test@test.com").GetAwaiter().GetResult();
        GitScannerService.RunGitAsync(_repoPath, "config user.name Test").GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ScanAsync_NonGitDirectory_ReturnsEmpty()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "not-a-repo-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyDir);

        var result = await _sut.ScanAsync(emptyDir);

        Assert.Empty(result);
        Directory.Delete(emptyDir, true);
    }

    [Fact]
    public async Task ScanAsync_RepoWithNoCommits_ReturnsEmpty()
    {
        var result = await _sut.ScanAsync(_repoPath);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_CommitWithCsFile_ReturnsChangeEvent()
    {
        await CommitFileAsync("Service.cs", "// initial\npublic class Service {}");

        var result = (await _sut.ScanAsync(_repoPath)).ToList();

        Assert.Single(result);
        Assert.Equal("Service.cs", result[0].FilePath);
        Assert.Equal(ChangeType.Added, result[0].ChangeType);
    }

    [Fact]
    public async Task ScanAsync_UnsupportedFileType_IsFiltered()
    {
        await CommitFileAsync("data.json", "{}");

        var result = await _sut.ScanAsync(_repoPath);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanAsync_MultipleSupportedFiles_ReturnsAll()
    {
        await CommitFileAsync("api.ts", "export const x = 1;");
        await CommitFileAsync("util.py", "def helper(): pass");

        var result = (await _sut.ScanAsync(_repoPath)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.FilePath == "api.ts");
        Assert.Contains(result, e => e.FilePath == "util.py");
    }

    [Fact]
    public async Task ScanAsync_ModifiedFile_ReturnsModifiedChangeType()
    {
        await CommitFileAsync("Service.cs", "// v1");
        var firstCommit = (await GitScannerService.RunGitAsync(_repoPath, "rev-parse HEAD")).Trim();

        await CommitFileAsync("Service.cs", "// v2 updated");

        var result = (await _sut.ScanAsync(_repoPath, firstCommit)).ToList();

        Assert.Single(result);
        Assert.Equal(ChangeType.Modified, result[0].ChangeType);
    }

    private async Task CommitFileAsync(string fileName, string content)
    {
        var filePath = Path.Combine(_repoPath, fileName);
        await File.WriteAllTextAsync(filePath, content);
        await GitScannerService.RunGitAsync(_repoPath, $"add {fileName}");
        await GitScannerService.RunGitAsync(_repoPath, $"commit -m \"add {fileName}\"");
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
            Directory.Delete(_repoPath, true);
    }
}
