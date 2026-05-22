using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

/// <summary>Handles GitHub webhook POST events at /api/github/webhook. On pull_request.closed with merged=true: validates the HMAC-SHA256 signature, fetches the PR's changed files, runs StaleDocDetectorService against those files, and posts a stale-doc report as a PR comment.</summary>
public static class GitHubWebhookHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<IResult> HandleAsync(HttpContext ctx, IStaleDocDetectorService detector, IClaudeService claude)
    {
        // Read raw body first — needed for HMAC validation before parsing.
        using var ms   = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var bodyBytes  = ms.ToArray();
        var bodyText   = Encoding.UTF8.GetString(bodyBytes);

        if (!ValidateSignature(ctx.Request, bodyBytes))
            return Results.Unauthorized();

        var eventType = ctx.Request.Headers["X-GitHub-Event"].ToString();
        if (eventType != "pull_request")
            return Results.Ok("event ignored");

        JsonElement root;
        try { root = JsonDocument.Parse(bodyText).RootElement; }
        catch { return Results.BadRequest("invalid JSON"); }

        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        var merged  = root.TryGetProperty("pull_request", out var pr)
                   && pr.TryGetProperty("merged", out var m)
                   && m.GetBoolean();

        if (action != "closed" || !merged)
            return Results.Ok("event ignored");

        var prNumber  = pr.GetProperty("number").GetInt32();
        var prTitle   = pr.TryGetProperty("title", out var t) ? t.GetString() ?? $"PR #{prNumber}" : $"PR #{prNumber}";
        var prUrl     = pr.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        var repoOwner = root.GetProperty("repository").GetProperty("owner").GetProperty("login").GetString()!;
        var repoName  = root.GetProperty("repository").GetProperty("name").GetString()!;
        var repoPath  = Environment.GetEnvironmentVariable("LIVINGDOCS_REPO_PATH");

        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            // No local repo configured — nothing to scan.
            return Results.Ok("LIVINGDOCS_REPO_PATH not set, skipping scan");
        }

        using var http = BuildGitHubClient();

        var changedFiles = await GetPrFilesAsync(http, repoOwner, repoName, prNumber);
        if (changedFiles.Count == 0)
            return Results.Ok("no supported files changed");

        // Fetch diff and run AI impact analysis in parallel with staleness scan.
        var diffTask     = GetPrDiffAsync(http, repoOwner, repoName, prNumber);
        var (report, staleDocs) = await BuildReportAsync(detector, repoPath, changedFiles, repoOwner, repoName);

        var rawDiff       = await diffTask;
        var impactSummary = rawDiff.Length > 0
            ? await claude.AnalysePrDiffAsync(rawDiff)
            : string.Empty;

        // Semantic doc mapping — find .md docs related to this change
        var mdCandidates = ScanMarkdownDocs(repoPath);
        var affectedDocs = mdCandidates.Count > 0 && impactSummary.Length > 0
            ? await claude.FindAffectedDocsAsync(impactSummary, changedFiles, mdCandidates)
            : (IReadOnlyList<AffectedDoc>)[];

        var fullReport = BuildFullReport(impactSummary, affectedDocs, report);
        await PostPrCommentAsync(http, repoOwner, repoName, prNumber, fullReport);
        await SlackNotifier.NotifyStaleDocsAsync(prUrl, prTitle, prNumber, $"{repoOwner}/{repoName}", staleDocs, impactSummary, affectedDocs, repoPath);

        return Results.Ok("comment posted");
    }

    // ── HMAC validation ───────────────────────────────────────────────────

    private static bool ValidateSignature(HttpRequest request, byte[] body)
    {
        var secret = Environment.GetEnvironmentVariable("GITHUB_WEBHOOK_SECRET")
                  ?? Environment.GetEnvironmentVariable("LIVINGDOCS_WEBHOOK_SECRET");
        if (string.IsNullOrEmpty(secret)) return false;

        var signature = request.Headers["X-Hub-Signature-256"].ToString();
        if (!signature.StartsWith("sha256=")) return false;

        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }

    // ── GitHub API helpers ────────────────────────────────────────────────

    private static HttpClient BuildGitHubClient()
    {
        var token  = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "LivingDocs-Webhook");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static readonly HashSet<string> SupportedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py" };

    private static async Task<List<string>> GetPrFilesAsync(
        HttpClient http, string owner, string repo, int prNumber)
    {
        try
        {
            var url      = $"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}/files?per_page=100";
            var json     = await http.GetStringAsync(url);
            var files    = JsonDocument.Parse(json).RootElement;
            var result   = new List<string>();

            foreach (var file in files.EnumerateArray())
            {
                var name = file.GetProperty("filename").GetString() ?? string.Empty;
                if (SupportedExt.Contains(Path.GetExtension(name)))
                    result.Add(name);
            }

            return result;
        }
        catch { return []; }
    }

    // ── PR diff fetch ─────────────────────────────────────────────────────

    private static async Task<string> GetPrDiffAsync(
        HttpClient http, string owner, string repo, int prNumber)
    {
        try
        {
            var url    = $"https://api.github.com/repos/{owner}/{repo}/pulls/{prNumber}";
            var req    = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/vnd.github.diff");
            var resp   = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static IReadOnlyList<DocCandidate> ScanMarkdownDocs(string repoPath)
    {
        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "node_modules", ".git", "bin", "obj", ".livingdocs" };

        try
        {
            return Directory
                .EnumerateFiles(repoPath, "*.md", SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar)
                              .Any(seg => skipDirs.Contains(seg)))
                .Take(30) // cap candidates to keep prompt size reasonable
                .Select(f =>
                {
                    var rel     = Path.GetRelativePath(repoPath, f);
                    var lines   = File.ReadLines(f).Take(20).ToList();
                    var title   = lines.FirstOrDefault(l => l.StartsWith("# "))?.TrimStart('#').Trim()
                                  ?? Path.GetFileNameWithoutExtension(f);
                    var snippet = string.Join(" ", lines
                        .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
                        .Take(3))
                        .Trim();
                    snippet = snippet.Length > 300 ? snippet[..300] : snippet;
                    return new DocCandidate(rel, title, snippet);
                })
                .ToList();
        }
        catch { return []; }
    }

    private static string BuildFullReport(string impactSummary, IReadOnlyList<AffectedDoc> affectedDocs, string staleReport)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(impactSummary))
        {
            sb.AppendLine("## 🤖 What changed");
            sb.AppendLine();
            sb.AppendLine(impactSummary.Trim());
            sb.AppendLine();
        }

        if (affectedDocs.Count > 0)
        {
            sb.AppendLine("## 📚 Related docs to review");
            sb.AppendLine();
            foreach (var doc in affectedDocs)
                sb.AppendLine($"- **`{doc.FilePath}`** — {doc.Reason}");
            sb.AppendLine();
        }

        if (sb.Length > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.Append(staleReport);
        return sb.ToString();
    }

    // ── Stale detection + report ──────────────────────────────────────────

    private static async Task<(string Report, IReadOnlyList<StaleDoc> StaleDocs)> BuildReportAsync(
        IStaleDocDetectorService detector,
        string repoPath,
        List<string> changedFiles,
        string owner,
        string repo)
    {
        var scanResult = await detector.DetectAsync(repoPath);

        var stale = scanResult.StaleDocs
            .Where(d => changedFiles.Any(f =>
                string.Equals(f, d.FilePath, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => d.StaleScore)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("## 📄 LivingDocs — Stale Doc Report");
        sb.AppendLine();
        sb.AppendLine($"Scanned **{changedFiles.Count}** file(s) changed in this PR.");
        sb.AppendLine();

        if (stale.Count == 0)
        {
            sb.AppendLine("✅ All documentation looks fresh — no action needed.");
            return (sb.ToString(), stale);
        }

        sb.AppendLine($"⚠️ **{stale.Count}** file(s) may have stale documentation:\n");

        foreach (var doc in stale)
        {
            var bar = new string('█', (int)(doc.StaleScore * 10)).PadRight(10, '░');
            sb.AppendLine($"### `{doc.FilePath}`");
            sb.AppendLine($"- Staleness: `[{bar}]` {doc.StaleScore:P0}");
            sb.AppendLine($"- Doc last updated : {doc.DocLastUpdated:yyyy-MM-dd}");
            sb.AppendLine($"- Code last changed: {doc.CodeLastChanged:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("**Suggested action:**");
            sb.AppendLine("```");
            sb.AppendLine($"suggest_doc_update on {repoPath} {doc.FilePath}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("_Generated by [LivingDocs](https://github.com/projectx78/livingdocs)_");

        return (sb.ToString(), stale);
    }

    private static async Task PostPrCommentAsync(
        HttpClient http, string owner, string repo, int prNumber, string body)
    {
        try
        {
            var url     = $"https://api.github.com/repos/{owner}/{repo}/issues/{prNumber}/comments";
            var payload = JsonSerializer.Serialize(new { body });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await http.PostAsync(url, content);
        }
        catch { /* best-effort — don't fail the webhook response */ }
    }
}
