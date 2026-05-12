namespace LivingDocs.Core.Models;

public class StaleDoc
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime DocLastUpdated { get; set; }
    public DateTime CodeLastChanged { get; set; }
    public float StaleScore { get; set; }
    public string? Suggestion { get; set; }
}
