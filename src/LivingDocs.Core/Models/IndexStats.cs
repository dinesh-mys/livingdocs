namespace LivingDocs.Core.Models;

public class IndexStats
{
    public int TotalChunks { get; set; }
    public DateTime? LastIndexed { get; set; }
    public string Provider { get; set; } = string.Empty;
}
