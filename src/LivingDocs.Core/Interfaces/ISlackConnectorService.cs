using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface ISlackConnectorService
{
    /// <summary>Fetches messages from a Slack channel and returns them as DocChunks ready for indexing.
    /// Requires SLACK_BOT_TOKEN. Groups replies into their parent thread so each thread becomes one chunk.</summary>
    Task<IReadOnlyList<DocChunk>> FetchChunksAsync(string channelId, int limit = 200);
}
