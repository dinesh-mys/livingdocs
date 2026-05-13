using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

public sealed class IndexService : IIndexService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py" };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".livingdocs" };

    private readonly IDocExtractorService         _extractor;
    private readonly ISemanticSearchServiceFactory _searchFactory;

    public IndexService(IDocExtractorService extractor, ISemanticSearchServiceFactory searchFactory)
    {
        _extractor     = extractor;
        _searchFactory = searchFactory;
    }

    public async Task<int> IndexRepoAsync(string repoPath)
    {
        await using var search = _searchFactory.Create(repoPath);

        var files = Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(f => ShouldIndex(repoPath, f));

        int total = 0;
        foreach (var file in files)
        {
            var rel    = Path.GetRelativePath(repoPath, file);
            var chunks = await _extractor.ExtractAsync(rel, await File.ReadAllTextAsync(file));

            await search.DeleteAsync(rel);
            foreach (var chunk in chunks)
            {
                await search.IndexAsync(chunk);
                total++;
            }
        }

        await search.FlushAsync();
        return total;
    }

    public async Task ReIndexFileAsync(string repoPath, string filePath)
    {
        await using var search = _searchFactory.Create(repoPath);

        await search.DeleteAsync(filePath);

        var fullPath = Path.Combine(repoPath, filePath);
        if (File.Exists(fullPath))
        {
            var chunks = await _extractor.ExtractAsync(filePath, await File.ReadAllTextAsync(fullPath));
            foreach (var chunk in chunks)
                await search.IndexAsync(chunk);
        }

        await search.FlushAsync();
    }

    private static bool ShouldIndex(string repoPath, string filePath)
    {
        if (!Extensions.Contains(Path.GetExtension(filePath))) return false;
        var parts = Path.GetRelativePath(repoPath, filePath)
                        .Split(Path.DirectorySeparatorChar);
        return !parts.Any(p => SkipDirs.Contains(p));
    }
}
