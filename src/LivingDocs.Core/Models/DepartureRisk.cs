namespace LivingDocs.Core.Models;

public record AuthorOwnership(string Author, int CommitCount, double Percentage);

public record FileRisk(
    string FilePath,
    int TotalCommits,
    IReadOnlyList<AuthorOwnership> TopAuthors)
{
    public bool IsSinglePointOfKnowledge =>
        TotalCommits >= 5 && TopAuthors.Count > 0 && TopAuthors[0].Percentage >= 0.60;
}

public record AuthorRiskSummary(
    string Author,
    IReadOnlyList<string> OwnedFiles);

public record DepartureRiskReport(
    string RepoPath,
    int FilesAnalysed,
    IReadOnlyList<FileRisk> RiskyFiles,
    IReadOnlyList<AuthorRiskSummary> AuthorSummaries);
