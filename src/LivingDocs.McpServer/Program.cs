using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using LivingDocs.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

LoadDotEnv();

// CLI mode
if (args is ["scan", var repoPath])
{
    await RunScanAsync(repoPath);
    return;
}

if (args is ["index", var indexRepo])
{
    await RunIndexAsync(indexRepo);
    return;
}

if (args is ["reindex", var reindexRepo, var changedFile])
{
    await RunReindexAsync(reindexRepo, changedFile);
    return;
}

if (args is ["install-hooks", var hooksRepo])
{
    await RunInstallHooksAsync(hooksRepo);
    return;
}

if (args is ["query", var queryRepo, .. var questionParts])
{
    await RunQueryAsync(queryRepo, string.Join(" ", questionParts));
    return;
}

if (args is ["watch", var watchRepo])
{
    await RunWatchAsync(watchRepo);
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
    .AddSingleton<ILicenseService>(_ => new LicenseService(new HttpClient()))
    .AddSingleton<ISemanticSearchServiceFactory, ClaudeAssistedSearchFactory>()
    .AddSingleton<IIndexService, IndexService>()
    .AddSingleton<IDocWriterService, DocWriterService>();

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

static async Task RunIndexAsync(string repoPath)
{
    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    Console.WriteLine($"Indexing {repoPath} ...");
    var claude   = TryCreateClaude();
    var factory  = new ClaudeAssistedSearchFactory(claude);
    var indexer  = new IndexService(new DocExtractorService(), factory);
    var total    = await indexer.IndexRepoAsync(repoPath);
    Console.WriteLine($"Indexed {total} chunk(s). Semantic search ready.");
}

static async Task RunReindexAsync(string repoPath, string filePath)
{
    if (!Directory.Exists(repoPath)) return;

    var claude  = TryCreateClaude();
    var factory = new ClaudeAssistedSearchFactory(claude);
    var indexer = new IndexService(new DocExtractorService(), factory);
    await indexer.ReIndexFileAsync(repoPath, filePath);
    Console.WriteLine($"Re-indexed: {filePath}");
}

static async Task RunQueryAsync(string repoPath, string question)
{
    if (string.IsNullOrWhiteSpace(question)) { Console.Error.WriteLine("Usage: query <repo> <question>"); return; }
    Console.WriteLine($"Querying: {question}");
    var claude  = TryCreateClaude();
    var factory = new ClaudeAssistedSearchFactory(claude);
    await using var search = factory.Create(repoPath);
    var stats = search.GetStats();
    Console.WriteLine($"Index: {stats.TotalChunks} chunks (provider: {stats.Provider})");
    var results = await search.SearchAsync(question, topK: 10);
    Console.WriteLine($"BM25 results: {results.Count}");
    if (results.Count == 0) { Console.WriteLine("No relevant docs found."); return; }
    var answer = await claude.QueryDocsAsync(question, results.Select(r => r.Chunk));
    Console.WriteLine(answer);
}

// Claude is only needed at search/rerank time, not during index builds.
static IClaudeService TryCreateClaude()
{
    try   { return new ClaudeService(new HttpClient()); }
    catch  { return new NullClaudeService(); }
}

static async Task RunInstallHooksAsync(string repoPath)
{
    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    var gitDir = Path.Combine(repoPath, ".git");
    if (!Directory.Exists(gitDir))
    {
        Console.Error.WriteLine($"Not a git repository: {repoPath}");
        Environment.Exit(1);
    }

    var hooksDir = Path.Combine(gitDir, "hooks");
    Directory.CreateDirectory(hooksDir);

    var hookPath = Path.Combine(hooksDir, "post-commit");
    var script   = """
        #!/bin/sh
        # LivingDocs — re-index changed documentation on every commit.
        REPO_ROOT=$(git rev-parse --show-toplevel)
        git diff --name-only HEAD~1 HEAD 2>/dev/null | while IFS= read -r file; do
          case "$file" in
            *.cs|*.ts|*.tsx|*.js|*.jsx|*.py)
              livingdocs-mcp reindex "$REPO_ROOT" "$file" 2>/dev/null
              ;;
          esac
        done
        """;

    await File.WriteAllTextAsync(hookPath, script);

    // chmod +x on Unix
    if (!OperatingSystem.IsWindows())
    {
        var chmod = System.Diagnostics.Process.Start("chmod", $"+x {hookPath}");
        await chmod!.WaitForExitAsync();
    }

    Console.WriteLine($"Installed post-commit hook at {hookPath}");
    Console.WriteLine("The index will update automatically on every commit.");
}

static async Task RunWatchAsync(string repoPath)
{
    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    var claude    = TryCreateClaude();
    var factory   = new ClaudeAssistedSearchFactory(claude);
    var indexer   = new IndexService(new DocExtractorService(), factory);
    var scanner   = new GitScannerService();
    var extractor = new DocExtractorService();
    var detector  = new StaleDocDetectorService(scanner, extractor);

    // Debounce table — ignore duplicate events within 1 second.
    var lastFired = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var watcher = new FileSystemWatcher(repoPath)
    {
        IncludeSubdirectories = true,
        NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
        EnableRaisingEvents   = true
    };

    var queue = new System.Collections.Concurrent.ConcurrentQueue<string>();

    void Enqueue(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        if (!new[] { ".cs", ".ts", ".tsx", ".js", ".jsx", ".py" }.Contains(ext)) return;

        var rel = Path.GetRelativePath(repoPath, fullPath);
        if (rel.Split(Path.DirectorySeparatorChar)
               .Any(p => p is "bin" or "obj" or "node_modules" or ".git" or ".livingdocs"))
            return;

        if (lastFired.TryGetValue(rel, out var last) && (DateTime.UtcNow - last).TotalSeconds < 1)
            return;

        lastFired[rel] = DateTime.UtcNow;
        queue.Enqueue(rel);
    }

    watcher.Changed += (_, e) => Enqueue(e.FullPath);
    watcher.Created += (_, e) => Enqueue(e.FullPath);

    Console.WriteLine($"Watching {repoPath}");
    Console.WriteLine("Press Ctrl+C to stop.\n");

    while (!cts.Token.IsCancellationRequested)
    {
        if (queue.TryDequeue(out var relPath))
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            Console.Write($"[{timestamp}] {relPath} saved — ");

            try
            {
                await indexer.ReIndexFileAsync(repoPath, relPath);

                var result   = await detector.DetectAsync(repoPath);
                var fileStale = result.StaleDocs
                    .Where(d => string.Equals(d.FilePath, relPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.StaleScore)
                    .ToList();

                if (fileStale.Count == 0)
                {
                    Console.WriteLine("re-indexed, docs look fresh");
                }
                else
                {
                    Console.WriteLine($"re-indexed | {fileStale.Count} stale doc(s):");
                    foreach (var doc in fileStale)
                    {
                        var bar = new string('█', (int)(doc.StaleScore * 10)).PadRight(10, '░');
                        Console.WriteLine($"  [{bar}] {doc.StaleScore:P0}  " +
                            $"doc:{doc.DocLastUpdated:yyyy-MM-dd}  " +
                            $"code:{doc.CodeLastChanged:yyyy-MM-dd}");
                    }
                    Console.WriteLine($"  → run: suggest_doc_update on {repoPath} {relPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error — {ex.Message}");
            }

            Console.WriteLine();
        }
        else
        {
            await Task.Delay(200, cts.Token).ContinueWith(_ => { });
        }
    }

    Console.WriteLine("\nWatcher stopped.");
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
    public Task<string> CompleteAsync(string prompt, int maxTokens = 1024, string? model = null) => Task.FromResult(Msg);
    public Task<DocSuggestion> SuggestDocUpdateAsync(ChangeEvent change, DocChunk existingDoc, string? symbolContext = null)
        => Task.FromResult(new DocSuggestion(Msg, 0f, NeedsReview: true));
    public Task<string> QueryDocsAsync(string question, IEnumerable<DocChunk> docs) => Task.FromResult(Msg);
}
