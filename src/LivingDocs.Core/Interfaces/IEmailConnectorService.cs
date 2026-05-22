using LivingDocs.Core.Models;

namespace LivingDocs.Core.Interfaces;

public interface IEmailConnectorService
{
    /// <summary>Fetches emails from an IMAP mailbox folder and returns them as DocChunks ready for
    /// indexing. Requires IMAP_HOST, IMAP_USERNAME, IMAP_PASSWORD. Optional: IMAP_PORT (default 993).</summary>
    Task<IReadOnlyList<DocChunk>> FetchChunksAsync(string folder = "INBOX", int limit = 100);
}
