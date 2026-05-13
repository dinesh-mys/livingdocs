using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface ISemanticSearchService : IAsyncDisposable
{
    Task IndexAsync(DocChunk chunk);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 10);
    Task DeleteAsync(string filePath);
    Task FlushAsync();
    IndexStats GetStats();
}
