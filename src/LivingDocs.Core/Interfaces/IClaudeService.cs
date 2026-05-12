using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IClaudeService
{
    Task<string> CompleteAsync(string prompt, int maxTokens = 1024);
    Task<string> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc);
    Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs);
}
