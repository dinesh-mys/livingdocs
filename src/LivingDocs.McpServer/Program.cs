using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using LivingDocs.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

LoadDotEnv();

// CLI mode: livingdocs scan <path>
if (args is ["scan", var repoPath])
{
    await RunScanAsync(repoPath);
    return;
}

// MCP server mode — stdio transport (Claude Desktop / Claude Code)
// Logging is suppressed so host output never corrupts the stdio JSON-RPC stream.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();

builder.Services
    .AddSingleton<IGitScannerService, GitScannerService>()
    .AddSingleton<IDocExtractorService, DocExtractorService>()
    .AddSingleton<IStaleDocDetectorService, StaleDocDetectorService>()
    .AddSingleton<IClaudeService>(_ =>
    {
        try   { return new ClaudeService(new HttpClient()); }
        catch  { return new NullClaudeService(); }
    })
    .AddSingleton<ILicenseService>(_ => new LicenseService(new HttpClient()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(LivingDocsTools).Assembly);

await builder.Build().RunAsync();

// ── CLI helpers ───────────────────────────────────────────────────────────

static async Task RunScanAsync(string repoPath)
{
    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    Console.WriteLine($"Scanning {repoPath} ...");

    var scanner   = new GitScannerService();
    var extractor = new DocExtractorService();
    var detector  = new StaleDocDetectorService(scanner, extractor);
    var result    = await detector.DetectAsync(repoPath);

    Console.WriteLine($"Files examined : {result.TotalFiles}");
    Console.WriteLine($"Stale docs     : {result.StaleDocs.Count}");

    if (result.StaleDocs.Count == 0)
    {
        Console.WriteLine("All documentation looks fresh.");
        return;
    }

    Console.WriteLine();
    foreach (var doc in result.StaleDocs.OrderByDescending(d => d.StaleScore))
    {
        var bar = new string('█', (int)(doc.StaleScore * 10)).PadRight(10);
        Console.WriteLine($"  [{bar}] {doc.StaleScore,4:P0}  {doc.FilePath}");
        Console.WriteLine($"           doc updated : {doc.DocLastUpdated:yyyy-MM-dd}");
        Console.WriteLine($"           code changed: {doc.CodeLastChanged:yyyy-MM-dd}");
    }
}

static void LoadDotEnv()
{
    var path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    if (!File.Exists(path)) return;

    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;

        var idx = trimmed.IndexOf('=');
        var key = trimmed[..idx].Trim();
        var val = trimmed[(idx + 1)..].Trim().Trim('"');

        if (!string.IsNullOrEmpty(key))
            Environment.SetEnvironmentVariable(key, val);
    }
}

// Returns a friendly error when ANTHROPIC_API_KEY is not set
file sealed class NullClaudeService : IClaudeService
{
    private const string Msg = "ANTHROPIC_API_KEY is not set. Add it to your .env file and restart the server.";
    public Task<string> CompleteAsync(string prompt, int maxTokens = 1024) => Task.FromResult(Msg);
    public Task<string> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc) => Task.FromResult(Msg);
}
