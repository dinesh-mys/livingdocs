namespace LivingDocs.Core.Models;

public class ScanResult
{
    public string RepoPath { get; set; } = string.Empty;
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public List<StaleDoc> StaleDocs { get; set; } = new();
    public int TotalFiles { get; set; }
}
