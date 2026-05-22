namespace LivingDocs.Core.Models;

/// <summary>A documentation file identified as semantically related to a code change.</summary>
public record DocCandidate(string FilePath, string Title, string Snippet);

/// <summary>A doc Claude determined is likely affected by a PR, with a human-readable reason.</summary>
public record AffectedDoc(string FilePath, string Title, string Reason);
