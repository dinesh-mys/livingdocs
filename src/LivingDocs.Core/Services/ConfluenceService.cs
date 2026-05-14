using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

/// <summary>Syncs documentation to Confluence Cloud via the REST API. Requires CONFLUENCE_BASE_URL, CONFLUENCE_EMAIL, and CONFLUENCE_API_TOKEN environment variables.</summary>
public class ConfluenceService : IConfluenceService
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    public ConfluenceService(HttpClient http)
    {
        _http    = http;
        _baseUrl = (Environment.GetEnvironmentVariable("CONFLUENCE_BASE_URL") ?? string.Empty)
                   .TrimEnd('/');

        var email = Environment.GetEnvironmentVariable("CONFLUENCE_EMAIL") ?? string.Empty;
        var token = Environment.GetEnvironmentVariable("CONFLUENCE_API_TOKEN") ?? string.Empty;

        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(token))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", creds);
        }

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ConfluencePage?> FindPageAsync(string spaceKey, string title)
    {
        var encoded = Uri.EscapeDataString(title);
        var url     = $"{_baseUrl}/rest/api/content?title={encoded}&spaceKey={spaceKey}&expand=version&limit=1";

        var json    = await _http.GetStringAsync(url);
        var doc     = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");

        if (results.GetArrayLength() == 0) return null;

        var page    = results[0];
        var id      = page.GetProperty("id").GetString()!;
        var ver     = page.GetProperty("version").GetProperty("number").GetInt32();
        var webUrl  = $"{_baseUrl}/wiki/spaces/{spaceKey}/pages/{id}";

        return new ConfluencePage(id, title, ver, webUrl);
    }

    public async Task<string> UpsertPageAsync(string spaceKey, string title, string htmlBody)
    {
        var existing = await FindPageAsync(spaceKey, title);

        if (existing is not null)
            return await UpdatePageAsync(existing, htmlBody);

        return await CreatePageAsync(spaceKey, title, htmlBody);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> UpdatePageAsync(ConfluencePage page, string htmlBody)
    {
        var payload = JsonSerializer.Serialize(new
        {
            version = new { number = page.Version + 1 },
            title   = page.Title,
            type    = "page",
            body    = new { storage = new { value = htmlBody, representation = "storage" } }
        });

        var response = await _http.PutAsync(
            $"{_baseUrl}/rest/api/content/{page.Id}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        return page.Url;
    }

    private async Task<string> CreatePageAsync(string spaceKey, string title, string htmlBody)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type  = "page",
            title = title,
            space = new { key = spaceKey },
            body  = new { storage = new { value = htmlBody, representation = "storage" } }
        });

        var response = await _http.PostAsync(
            $"{_baseUrl}/rest/api/content",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();

        var json    = await response.Content.ReadAsStringAsync();
        var doc     = JsonDocument.Parse(json);
        var id      = doc.RootElement.GetProperty("id").GetString()!;
        return $"{_baseUrl}/wiki/spaces/{spaceKey}/pages/{id}";
    }
}
