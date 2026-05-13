namespace LivingDocs.Core.Interfaces;

public interface ISemanticSearchServiceFactory
{
    ISemanticSearchService Create(string repoPath);
}
