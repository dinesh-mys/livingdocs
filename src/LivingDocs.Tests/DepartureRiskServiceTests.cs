using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class DepartureRiskServiceTests : IDisposable
{
    private readonly string _repoPath;

    public DepartureRiskServiceTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "ld-risk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoPath);
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Alice");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, true); } catch { }
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory       = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private async Task CommitFileAs(string relPath, string content, string authorName, string authorEmail)
    {
        var fullPath = Path.Combine(_repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        RunGit($"-c user.name=\"{authorName}\" -c user.email=\"{authorEmail}\" commit --allow-empty-message -m \"chg\" --author=\"{authorName} <{authorEmail}>\" --no-edit");
        // Need to stage first
        RunGit($"add \"{relPath}\"");
        RunGit($"-c user.name=\"{authorName}\" -c user.email=\"{authorEmail}\" commit -m \"update {relPath}\"");
    }

    // Simpler approach: set env vars for author per commit
    private async Task CommitAs(string relPath, string content, string name, string email)
    {
        var fullPath = Path.Combine(_repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);

        var addPsi = new System.Diagnostics.ProcessStartInfo("git", $"add \"{relPath}\"")
        {
            WorkingDirectory = _repoPath, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        using (var p = System.Diagnostics.Process.Start(addPsi)!) p.WaitForExit();

        var commitArgs = $"commit -m \"update {relPath}\"";
        var commitPsi = new System.Diagnostics.ProcessStartInfo("git", commitArgs)
        {
            WorkingDirectory = _repoPath, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        };
        commitPsi.Environment["GIT_AUTHOR_NAME"]     = name;
        commitPsi.Environment["GIT_AUTHOR_EMAIL"]    = email;
        commitPsi.Environment["GIT_COMMITTER_NAME"]  = name;
        commitPsi.Environment["GIT_COMMITTER_EMAIL"] = email;
        using (var p = System.Diagnostics.Process.Start(commitPsi)!) p.WaitForExit();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyseAsync_DirectoryNotFound_ReturnsEmptyReport()
    {
        var svc    = new DepartureRiskService();
        var report = await svc.AnalyseAsync("/no/such/dir");
        Assert.Equal(0, report.FilesAnalysed);
        Assert.Empty(report.RiskyFiles);
    }

    [Fact]
    public async Task AnalyseAsync_FileWithFewCommits_NotFlagged()
    {
        // Only 2 commits — below the 5-commit threshold
        await CommitAs("src/Foo.cs", "public class Foo {}", "Alice", "alice@test.com");
        await CommitAs("src/Foo.cs", "public class Foo { public void A() {} }", "Alice", "alice@test.com");

        var svc    = new DepartureRiskService();
        var report = await svc.AnalyseAsync(_repoPath);

        Assert.Empty(report.RiskyFiles);
    }

    [Fact]
    public async Task AnalyseAsync_TestFiles_AreExcluded()
    {
        for (int i = 0; i < 6; i++)
            await CommitAs("src/FooTests.cs", $"public class FooTests {{ public void T{i}() {{}} }}", "Alice", "alice@test.com");

        var svc    = new DepartureRiskService();
        var report = await svc.AnalyseAsync(_repoPath);

        Assert.Equal(0, report.FilesAnalysed);
        Assert.Empty(report.RiskyFiles);
    }
}
