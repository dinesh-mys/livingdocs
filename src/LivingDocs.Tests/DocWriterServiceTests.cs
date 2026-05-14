using LivingDocs.Core.Models;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class DocWriterServiceTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"ld-test-{Guid.NewGuid()}");
    private readonly DocWriterService _sut = new();

    public DocWriterServiceTests() => Directory.CreateDirectory(_repoPath);

    public void Dispose() => Directory.Delete(_repoPath, recursive: true);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string WriteFile(string relativePath, string[] lines)
    {
        var full = Path.Combine(_repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, lines);
        return relativePath;
    }

    private static DocChunk Chunk(string path, int line) =>
        new() { FilePath = path, LineNumber = line };

    // ── C# XML ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBack_CSharpXml_SingleLine_ReplacesComment()
    {
        var file = WriteFile("src/Foo.cs", [
            "/// Old summary.",
            "public void Foo() {}"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "New summary.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("/// <summary>New summary.</summary>", lines[0]);
        Assert.Equal("public void Foo() {}", lines[1]);
    }

    [Fact]
    public async Task WriteBack_CSharpXml_MultiLine_ReplacesWholeBlock()
    {
        var file = WriteFile("src/Bar.cs", [
            "/// First line.",
            "/// Second line.",
            "public void Bar() {}"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "Replacement.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("/// <summary>Replacement.</summary>", lines[0]);
        Assert.Equal("public void Bar() {}", lines[1]);
    }

    [Fact]
    public async Task WriteBack_CSharpXml_PreservesIndentation()
    {
        var file = WriteFile("src/Baz.cs", [
            "    /// Old indented.",
            "    public void Baz() {}"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "New indented.");

        var result = (await File.ReadAllLinesAsync(Path.Combine(_repoPath, file)))[0];
        Assert.StartsWith("    ///", result);
    }

    // ── JSDoc ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBack_JsDoc_SingleLine_ReplacesComment()
    {
        var file = WriteFile("src/foo.ts", [
            "/** Old jsdoc. */",
            "function foo() {}"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "New jsdoc.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("/** New jsdoc. */", lines[0]);
    }

    [Fact]
    public async Task WriteBack_JsDoc_MultiLine_ReplacesBlock()
    {
        var file = WriteFile("src/multi.ts", [
            "/**",
            " * Old line one.",
            " * Old line two.",
            " */",
            "function bar() {}"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "Replacement.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("/** Replacement. */", lines[0]);
        Assert.Equal("function bar() {}", lines[1]);
    }

    // ── Python ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBack_Python_SingleLine_ReplacesDocstring()
    {
        var file = WriteFile("src/foo.py", [
            "    \"\"\"Old docstring.\"\"\"",
            "    pass"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "New docstring.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("    \"\"\"New docstring.\"\"\"", lines[0]);
    }

    [Fact]
    public async Task WriteBack_Python_MultiLine_ReplacesBlock()
    {
        var file = WriteFile("src/multi.py", [
            "    \"\"\"",
            "    Old line.",
            "    \"\"\"",
            "    pass"
        ]);

        await _sut.WriteBackAsync(_repoPath, file, Chunk(file, 1), "Replacement.");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_repoPath, file));
        Assert.Equal("    \"\"\"Replacement.\"\"\"", lines[0]);
        Assert.Equal("    pass", lines[1]);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBack_FileNotFound_Throws()
    {
        var chunk = Chunk("nonexistent.cs", 1);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _sut.WriteBackAsync(_repoPath, "nonexistent.cs", chunk, "text"));
    }

    [Fact]
    public async Task WriteBack_LineOutOfRange_Throws()
    {
        var file = WriteFile("src/Short.cs", ["/// Only one line."]);
        var chunk = Chunk(file, 99);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.WriteBackAsync(_repoPath, file, chunk, "text"));
    }
}
