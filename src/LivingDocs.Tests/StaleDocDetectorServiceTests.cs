using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class StaleDocDetectorServiceTests
{
    // ── ComputeStaleScore (pure logic) ────────────────────────────────────

    [Fact]
    public void ComputeStaleScore_SameDate_ReturnsZero()
    {
        var t = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0f, StaleDocDetectorService.ComputeStaleScore(t, t));
    }

    [Fact]
    public void ComputeStaleScore_Within7Days_ReturnsZero()
    {
        var doc  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = doc.AddDays(6);
        Assert.Equal(0f, StaleDocDetectorService.ComputeStaleScore(code, doc));
    }

    [Fact]
    public void ComputeStaleScore_CodeBeforeDoc_ReturnsZero()
    {
        var doc  = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = doc.AddDays(-10);
        Assert.Equal(0f, StaleDocDetectorService.ComputeStaleScore(code, doc));
    }

    [Fact]
    public void ComputeStaleScore_At90Days_ReturnsOne()
    {
        var doc  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = doc.AddDays(90);
        Assert.Equal(1f, StaleDocDetectorService.ComputeStaleScore(code, doc));
    }

    [Fact]
    public void ComputeStaleScore_Beyond90Days_ClampsToOne()
    {
        var doc  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = doc.AddDays(200);
        Assert.Equal(1f, StaleDocDetectorService.ComputeStaleScore(code, doc));
    }

    [Fact]
    public void ComputeStaleScore_At48DaysAndHalf_ReturnsMidRange()
    {
        // days = 48.5  =>  (48.5 - 7) / 83 ≈ 0.5
        var doc  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var code = doc.AddDays(48.5);
        var score = StaleDocDetectorService.ComputeStaleScore(code, doc);
        Assert.InRange(score, 0.49f, 0.51f);
    }

    // ── DetectAsync (integration via fakes) ───────────────────────────────

    private static StaleDocDetectorService Build(
        IEnumerable<ChangeEvent> changes,
        IEnumerable<DocChunk> chunks,
        Dictionary<(string file, int line), DateTime>? blame = null,
        Dictionary<string, string>? files = null)
    {
        var scanner   = new FakeScanner(changes);
        var extractor = new FakeExtractor(chunks);
        return new TestableDetector(scanner, extractor, blame ?? [], files ?? []);
    }

    [Fact]
    public async Task DetectAsync_NoChanges_ReturnsEmptyResult()
    {
        var sut = Build([], []);

        var result = await sut.DetectAsync("/repo");

        Assert.Empty(result.StaleDocs);
        Assert.Equal(0, result.TotalFiles);
        Assert.Equal("/repo", result.RepoPath);
    }

    [Fact]
    public async Task DetectAsync_ChangedFileNoDocChunks_ReturnsEmptyResult()
    {
        var change = MakeChange("src/Tax.cs", new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var sut    = Build([change], chunks: []);

        var result = await sut.DetectAsync("/repo");

        Assert.Empty(result.StaleDocs);
    }

    [Fact]
    public async Task DetectAsync_DocFreshRelativeToChange_ReturnsNoStaleDocs()
    {
        var docDate  = new DateTime(2025, 3, 30, 0, 0, 0, DateTimeKind.Utc);
        var codeDate = docDate.AddDays(3);           // only 3 days later → not stale

        var change = MakeChange("src/Tax.cs", codeDate);
        var chunk  = MakeChunk("src/Tax.cs", lineNumber: 1);

        var sut = Build(
            [change],
            [chunk],
            blame: new() { { ("src/Tax.cs", 1), docDate } },
            files: new() { { "/repo/src/Tax.cs", "content" } });

        var result = await sut.DetectAsync("/repo");

        Assert.Empty(result.StaleDocs);
    }

    [Fact]
    public async Task DetectAsync_DocStale_ReturnsSingleStaleDoc()
    {
        var docDate  = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var codeDate = docDate.AddDays(90);          // 90 days → score = 1.0

        var change = MakeChange("src/Tax.cs", codeDate);
        var chunk  = MakeChunk("src/Tax.cs", lineNumber: 1);

        var sut = Build(
            [change],
            [chunk],
            blame: new() { { ("src/Tax.cs", 1), docDate } },
            files: new() { { "/repo/src/Tax.cs", "content" } });

        var result = await sut.DetectAsync("/repo");

        Assert.Single(result.StaleDocs);
        Assert.Equal("src/Tax.cs", result.StaleDocs[0].FilePath);
        Assert.Equal(1f, result.StaleDocs[0].StaleScore);
        Assert.Equal(1, result.TotalFiles);
    }

    [Fact]
    public async Task DetectAsync_MultipleChanges_KeepsMostRecent()
    {
        var docDate   = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldChange = MakeChange("src/Tax.cs", docDate.AddDays(5));   // fresh → not stale
        var newChange = MakeChange("src/Tax.cs", docDate.AddDays(90));  // stale

        var chunk = MakeChunk("src/Tax.cs", lineNumber: 1);

        var sut = Build(
            [oldChange, newChange],
            [chunk],
            blame: new() { { ("src/Tax.cs", 1), docDate } },
            files: new() { { "/repo/src/Tax.cs", "content" } });

        var result = await sut.DetectAsync("/repo");

        // Most-recent change (90 days) should win → stale
        Assert.Single(result.StaleDocs);
        Assert.Equal(1f, result.StaleDocs[0].StaleScore);
    }

    [Fact]
    public async Task DetectAsync_MissingFile_SkipsFile()
    {
        var change = MakeChange("src/Missing.cs", DateTime.UtcNow);
        var chunk  = MakeChunk("src/Missing.cs", lineNumber: 1);

        // No entry in files dict → ReadFileAsync returns empty
        var sut = Build([change], [chunk]);

        var result = await sut.DetectAsync("/repo");

        Assert.Empty(result.StaleDocs);
        Assert.Equal(0, result.TotalFiles);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ChangeEvent MakeChange(string filePath, DateTime timestamp) => new()
    {
        FilePath   = filePath,
        CommitHash = "abc",
        ChangedBy  = "dev",
        Timestamp  = timestamp,
        ChangeType = ChangeType.Modified
    };

    private static DocChunk MakeChunk(string filePath, int lineNumber) => new()
    {
        FilePath     = filePath,
        Content      = "/// Some doc.",
        Language     = "csharp",
        LineNumber   = lineNumber,
        LastUpdated  = DateTime.MinValue
    };
}

// ── Fakes ─────────────────────────────────────────────────────────────────

file sealed class FakeScanner(IEnumerable<ChangeEvent> events) : IGitScannerService
{
    public Task<IEnumerable<ChangeEvent>> ScanAsync(string repoPath, string? sinceCommit = null)
        => Task.FromResult(events);
}

file sealed class FakeExtractor(IEnumerable<DocChunk> chunks) : IDocExtractorService
{
    public Task<IEnumerable<DocChunk>> ExtractAsync(string filePath, string content)
        => Task.FromResult(chunks);
}

file sealed class TestableDetector(
    IGitScannerService scanner,
    IDocExtractorService extractor,
    Dictionary<(string file, int line), DateTime> blame,
    Dictionary<string, string> files)
    : StaleDocDetectorService(scanner, extractor)
{
    protected override Task<string> ReadFileAsync(string fullPath)
        => Task.FromResult(files.GetValueOrDefault(fullPath, string.Empty));

    protected override Task<DateTime> GetDocLastUpdatedAsync(
        string repoPath, string filePath, int lineNumber)
        => Task.FromResult(blame.GetValueOrDefault((filePath, lineNumber), DateTime.MinValue));
}
