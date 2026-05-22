using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface ITeamsConnectorService
{
    /// <summary>Fetches messages from a Microsoft Teams channel via the Graph API and returns them
    /// as DocChunks ready for indexing. Requires TEAMS_TENANT_ID, TEAMS_CLIENT_ID, TEAMS_CLIENT_SECRET.</summary>
    Task<IReadOnlyList<DocChunk>> FetchChunksAsync(string teamId, string channelId, int limit = 200);
}
