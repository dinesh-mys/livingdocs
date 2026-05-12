namespace LivingDocs.Core.Models;

public class DocChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
    public DateTime LastUpdated { get; set; }
    public string Language { get; set; } = string.Empty;
    public string? ParentSymbol { get; set; }
    public int LineNumber { get; set; }
}
