using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace LivingDocs.Core.Services;

public class EmailConnectorService : IEmailConnectorService
{
    public async Task<IReadOnlyList<DocChunk>> FetchChunksAsync(
        string folder = "INBOX", int limit = 100)
    {
        var host     = Environment.GetEnvironmentVariable("IMAP_HOST");
        var username = Environment.GetEnvironmentVariable("IMAP_USERNAME");
        var password = Environment.GetEnvironmentVariable("IMAP_PASSWORD");
        var portStr  = Environment.GetEnvironmentVariable("IMAP_PORT");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "IMAP_HOST, IMAP_USERNAME, and IMAP_PASSWORD must all be set.");

        int port = int.TryParse(portStr, out var p) ? p : 993;

        using var client = new ImapClient();
        await client.ConnectAsync(host, port, MailKit.Security.SecureSocketOptions.Auto);
        await client.AuthenticateAsync(username, password);

        var imapFolder = await OpenFolderAsync(client, folder);
        if (imapFolder is null)
            throw new InvalidOperationException($"IMAP folder not found: '{folder}'");

        var uids    = (await imapFolder.SearchAsync(SearchQuery.All)).Reverse().Take(limit).ToList();
        var chunks  = new List<DocChunk>();
        var seq     = 0;
        var source  = $"email://{folder.ToLowerInvariant().Replace(' ', '-')}";

        foreach (var uid in uids)
        {
            MimeMessage msg;
            try { msg = await imapFolder.GetMessageAsync(uid); }
            catch { continue; }

            var subject  = msg.Subject ?? "(no subject)";
            var body     = ExtractTextBody(msg);
            var from     = msg.From.Mailboxes.FirstOrDefault()?.Name
                        ?? msg.From.Mailboxes.FirstOrDefault()?.Address
                        ?? "Unknown";
            var date     = msg.Date.UtcDateTime;

            if (body.Length < 40) continue;

            // Cap body to avoid huge chunks blowing up the index
            var preview = body.Length > 1200 ? body[..1200] + "…" : body;
            var content = $"Subject: {subject}\nFrom: {from}\nDate: {date:yyyy-MM-dd}\n\n{preview}";

            chunks.Add(new DocChunk
            {
                Id           = Guid.NewGuid(),
                FilePath     = source,
                Language     = "email",
                Content      = content,
                ParentSymbol = subject,
                LineNumber   = seq++,
                LastUpdated  = date
            });
        }

        await client.DisconnectAsync(true);
        return chunks;
    }

    private static async Task<IMailFolder?> OpenFolderAsync(ImapClient client, string folderName)
    {
        // Try the personal namespace first, then fall back to full path
        try
        {
            var inbox = client.Inbox;
            if (string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
            {
                await inbox.OpenAsync(FolderAccess.ReadOnly);
                return inbox;
            }

            var namedFolder = await client.GetFolderAsync(folderName);
            await namedFolder.OpenAsync(FolderAccess.ReadOnly);
            return namedFolder;
        }
        catch { return null; }
    }

    private static string ExtractTextBody(MimeMessage msg)
    {
        // Prefer plain text; fall back to stripping HTML
        if (!string.IsNullOrWhiteSpace(msg.TextBody))
            return CleanBody(msg.TextBody);

        if (!string.IsNullOrWhiteSpace(msg.HtmlBody))
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(msg.HtmlBody, @"<[^>]+>", " ");
            stripped     = System.Net.WebUtility.HtmlDecode(stripped);
            stripped     = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s{2,}", " ").Trim();
            return CleanBody(stripped);
        }

        return string.Empty;
    }

    private static string CleanBody(string body)
    {
        // Remove email quoting (lines starting with >)
        var lines = body.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('>'))
            .Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join('\n', lines).Trim();
    }
}
