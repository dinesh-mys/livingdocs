namespace LivingDocs.Core.Models;

public class SearchResult
{
    public DocChunk Chunk { get; set; } = null!;
    public float Score { get; set; }
}
