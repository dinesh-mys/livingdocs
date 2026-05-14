using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IDocWriterService
{
    /// <summary>Replaces the doc comment for a single chunk in the source file on disk. Returns the number of lines replaced.</summary>
    Task<int> WriteBackAsync(string repoPath, string filePath, DocChunk chunk, string newCommentText);
}
