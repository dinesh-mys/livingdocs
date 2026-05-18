using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IDocWriterService
{
    /// <summary>Replaces the doc comment for a single chunk in the source file on disk. Returns the number of lines replaced.</summary>
    Task<int> WriteBackAsync(string repoPath, string filePath, DocChunk chunk, string newCommentText);

    /// <summary>Appends Claude-generated documentation for each symbol to docs/&lt;FileName&gt;.md with a UTC timestamp header. Returns the relative path of the written file.</summary>
    Task<string> WriteDocsAsync(string repoPath, string filePath, IReadOnlyList<(string Symbol, string Suggestion, float Confidence)> entries);
}
