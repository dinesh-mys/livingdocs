namespace LivingDocs.Core.Models;

public record DocGap(
    string FilePath,
    int CommitCount,
    DateTime LastChanged,
    string LastAuthor);

public record GapReport(
    string RepoPath,
    int TotalFiles,
    int DocumentedFiles,
    IReadOnlyList<DocGap> Gaps);
