namespace LivingDocs.Core.Models;

public enum ChangeType { Added, Modified, Deleted }

public class ChangeEvent
{
    public string CommitHash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Diff { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public ChangeType ChangeType { get; set; }
}
