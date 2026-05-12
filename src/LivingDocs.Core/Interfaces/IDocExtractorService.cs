using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IDocExtractorService
{
    Task<IEnumerable<DocChunk>> ExtractAsync(string filePath, string content);
}
