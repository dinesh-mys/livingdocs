namespace LivingDocs.Core.Models;

/// <summary>The result of a Claude-generated documentation update suggestion, including a confidence score and a flag for suggestions that should be reviewed by a human before applying.</summary>
public record DocSuggestion(
    string Suggestion,
    float  Confidence,
    bool   NeedsReview);
