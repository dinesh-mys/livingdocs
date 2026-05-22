using LivingDocs.Core.Models;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class GapDetectorServiceTests : IDisposable
{
    private readonly string _repoPath;

    public GapDetectorServiceTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "ld-gap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoPath);

        // Init a real git repo so git log works
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
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

    private async Task CommitFile(string relPath, string content)
    {
        var fullPath = Path.Combine(_repoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        RunGit($"add \"{relPath}\"");
        RunGit($"commit -m \"add {relPath}\"");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_DirectoryNotFound_ReturnsEmptyReport()
    {
        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync("/no/such/dir");
        Assert.Equal(0, report.TotalFiles);
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public async Task DetectAsync_FileWithDocs_IsNotAGap()
    {
        var content = """
            /// <summary>Does things.</summary>
            public class MyClass { }
            """;
        await CommitFile("src/MyClass.cs", content);

        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync(_repoPath);

        Assert.Equal(1, report.TotalFiles);
        Assert.Equal(1, report.DocumentedFiles);
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public async Task DetectAsync_FileWithoutDocs_IsAGap()
    {
        await CommitFile("src/NoDoc.cs", "public class NoDoc { }");

        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync(_repoPath);

        Assert.Equal(1, report.TotalFiles);
        Assert.Equal(0, report.DocumentedFiles);
        Assert.Single(report.Gaps);
        Assert.Equal("src/NoDoc.cs", report.Gaps[0].FilePath);
        Assert.Equal(1, report.Gaps[0].CommitCount);
    }

    [Fact]
    public async Task DetectAsync_MultipleCommits_CountIsCorrect()
    {
        await CommitFile("src/Hot.cs", "public class Hot { }");
        await CommitFile("src/Hot.cs", "public class Hot { public void A() {} }"); // second commit

        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync(_repoPath);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal(2, gap.CommitCount);
    }

    [Fact]
    public async Task DetectAsync_TestFiles_AreExcluded()
    {
        await CommitFile("src/FooTests.cs", "public class FooTests { }");
        await CommitFile("src/Foo.test.ts", "export {};");

        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync(_repoPath);

        Assert.Equal(0, report.TotalFiles);
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public async Task DetectAsync_GapsOrderedByCommitCountDescending()
    {
        // noisy.cs has 2 commits, quiet.cs has 1 — noisy should rank first
        await CommitFile("src/noisy.cs", "public class Noisy { }");
        await CommitFile("src/noisy.cs", "public class Noisy { public void X() {} }");
        await CommitFile("src/quiet.cs", "public class Quiet { }");

        var svc    = new GapDetectorService(new DocExtractorService());
        var report = await svc.DetectAsync(_repoPath);

        Assert.Equal(2, report.Gaps.Count);
        Assert.Equal("src/noisy.cs", report.Gaps[0].FilePath);
        Assert.Equal("src/quiet.cs", report.Gaps[1].FilePath);
    }
}
