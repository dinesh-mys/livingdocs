namespace LivingDocs.Core.Interfaces;

public interface IConfluenceService
{
    /// <summary>Finds a Confluence page by title in the given space. Returns null if not found.</summary>
    Task<ConfluencePage?> FindPageAsync(string spaceKey, string title);

    /// <summary>Creates or updates a Confluence page with the given HTML body. Returns the page URL.</summary>
    Task<string> UpsertPageAsync(string spaceKey, string title, string htmlBody);
}

public record ConfluencePage(string Id, string Title, int Version, string Url);
