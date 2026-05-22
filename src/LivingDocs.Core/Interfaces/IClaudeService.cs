using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IClaudeService
{
    Task<string> CompleteAsync(string prompt, int maxTokens = 1024, string? model = null);
    Task<DocSuggestion> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc, string? symbolContext = null);
    Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs);
    Task<string> AnalysePrDiffAsync(string diff);
}
