using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

public sealed class ClaudeAssistedSearchFactory : ISemanticSearchServiceFactory
{
    private readonly IClaudeService _claude;

    public ClaudeAssistedSearchFactory(IClaudeService claude) => _claude = claude;

    public ISemanticSearchService Create(string repoPath)
        => new ClaudeAssistedSearch(_claude, repoPath);
}
