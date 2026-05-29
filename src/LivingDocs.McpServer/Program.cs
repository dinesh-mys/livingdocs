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

if (args is ["init"] || args is ["init", _])
{
    await RunInitAsync(args.Length > 1 ? args[1] : null);
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
    .AddSingleton<ITelemetryService>(_ => new TelemetryService(new HttpClient()))
    .AddSingleton<IConfluenceService>(_ => new ConfluenceService(new HttpClient()))
    .AddSingleton<IGitHubOrgService>(_ => new GitHubOrgService(new HttpClient()))
    .AddSingleton<ISemanticSearchServiceFactory, ClaudeAssistedSearchFactory>()
    .AddSingleton<IIndexService, IndexService>()
    .AddSingleton<IDocWriterService, DocWriterService>()
    .AddSingleton<IGapDetectorService, GapDetectorService>()
    .AddSingleton<IDepartureRiskService, DepartureRiskService>()
    .AddSingleton<ISlackConnectorService>(_ => new SlackConnectorService(new HttpClient()))
    .AddSingleton<ITeamsConnectorService>(_ => new TeamsConnectorService(new HttpClient()))
    .AddSingleton<IEmailConnectorService, EmailConnectorService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(LivingDocsTools).Assembly);

var app = builder.Build();
app.Services.GetRequiredService<ITelemetryService>().Track("mcp_started");
await app.RunAsync();

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

    // Construct unconditionally so first_run fires on a user's first invocation
    // even if this repo has no doc comments (total == 0).
    var telemetry = new TelemetryService(new HttpClient(), noticeWriter: Console.Out);

    Console.WriteLine($"Indexing {repoPath} ...");
    var claude   = TryCreateClaude();
    var factory  = new ClaudeAssistedSearchFactory(claude);
    var indexer  = new IndexService(new DocExtractorService(), factory);
    var total    = await indexer.IndexRepoAsync(repoPath);
    Console.WriteLine($"Indexed {total} chunk(s). Semantic search ready.");
    if (total > 0)
    {
        await telemetry.TrackAsync("index_success",
            new Dictionary<string, string> { ["chunks"] = LivingDocsTools.Bucket(total) });
    }
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
    try
    {
        var answer = await claude.QueryDocsAsync(question, results.Select(r => r.Chunk));
        Console.WriteLine(answer);
    }
    catch (HttpRequestException ex) when (ex.Message.Contains("401") || ex.Message.Contains("authentication"))
    {
        Console.Error.WriteLine("Error: ANTHROPIC_API_KEY is invalid or not set.");
        Console.Error.WriteLine("Set it in your environment or add it to a .env file in the current directory.");
        Environment.Exit(1);
    }
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

static async Task RunInitAsync(string? suggestedPath)
{
    Console.WriteLine();
    Console.WriteLine("===========================================");
    Console.WriteLine("  LivingDocs — Setup Wizard");
    Console.WriteLine("===========================================");
    Console.WriteLine();

    // ── Step 1: Repo path ────────────────────────────────────────────────
    var defaultPath = suggestedPath ?? Directory.GetCurrentDirectory();
    Console.Write($"Step 1/3  Repository path [{defaultPath}]: ");
    var input = Console.ReadLine()?.Trim();
    var repoPath = string.IsNullOrEmpty(input) ? defaultPath : input;

    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"  ✗ Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
    {
        Console.Error.WriteLine($"  ✗ Not a git repository: {repoPath}");
        Environment.Exit(1);
    }

    Console.WriteLine($"  ✓ Repo: {repoPath}");
    Console.WriteLine();

    // ── Step 2: Anthropic API key ────────────────────────────────────────
    var existingKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty;
    string apiKey;

    if (!string.IsNullOrEmpty(existingKey))
    {
        Console.WriteLine($"Step 2/3  ANTHROPIC_API_KEY already set ({existingKey[..8]}...). Press Enter to keep it.");
        Console.Write("          Or enter a new key: ");
        var keyInput = Console.ReadLine()?.Trim();
        apiKey = string.IsNullOrEmpty(keyInput) ? existingKey : keyInput;
    }
    else
    {
        Console.Write("Step 2/3  ANTHROPIC_API_KEY (get one at console.anthropic.com): ");
        apiKey = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("  ⚠ No API key entered. AI features (suggest, query) will be disabled.");
            Console.WriteLine("    You can set ANTHROPIC_API_KEY in .env later.");
        }
        else
        {
            Console.WriteLine("  ✓ API key saved.");
        }
    }

    Console.WriteLine();

    // ── Step 3: Write .env ───────────────────────────────────────────────
    Console.WriteLine("Step 3/3  Writing configuration...");

    var envPath   = Path.Combine(repoPath, ".env");
    var envLines  = new List<string>
    {
        "# LivingDocs configuration — generated by livingdocs-mcp init",
        $"LIVINGDOCS_REPO_PATH={repoPath}",
    };
    if (!string.IsNullOrEmpty(apiKey))
        envLines.Add($"ANTHROPIC_API_KEY={apiKey}");

    await File.WriteAllLinesAsync(envPath, envLines);
    Console.WriteLine($"  ✓ Written: {envPath}");
    Console.WriteLine();

    // ── Optional: first index ────────────────────────────────────────────
    Console.Write("Build the search index now? (recommended, takes ~30s) [Y/n]: ");
    var runIndex = Console.ReadLine()?.Trim().ToLowerInvariant();

    if (runIndex is "" or "y" or "yes")
    {
        Console.WriteLine();
        await RunIndexAsync(repoPath);
        Console.WriteLine();
    }

    // ── Next steps ───────────────────────────────────────────────────────
    Console.WriteLine("===========================================");
    Console.WriteLine("  Setup complete! Next steps:");
    Console.WriteLine("===========================================");
    Console.WriteLine();
    Console.WriteLine("  Scan for stale docs:");
    Console.WriteLine($"    livingdocs-mcp scan {repoPath}");
    Console.WriteLine();
    Console.WriteLine("  Watch for changes (auto re-index on save):");
    Console.WriteLine($"    livingdocs-mcp watch {repoPath}");
    Console.WriteLine();
    Console.WriteLine("  Install post-commit hook (auto re-index on commit):");
    Console.WriteLine($"    livingdocs-mcp install-hooks {repoPath}");
    Console.WriteLine();
    Console.WriteLine("  Add to Claude Desktop (claude_desktop_config.json):");
    Console.WriteLine("    {");
    Console.WriteLine("      \"mcpServers\": {");
    Console.WriteLine("        \"livingdocs\": {");
    Console.WriteLine("          \"command\": \"livingdocs-mcp\"");
    Console.WriteLine("        }");
    Console.WriteLine("      }");
    Console.WriteLine("    }");
    Console.WriteLine();
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

        // Don't overwrite vars already set (e.g. from MCP env config or shell)
        if (!string.IsNullOrEmpty(key) && Environment.GetEnvironmentVariable(key) is null)
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
    public Task<string> AnalysePrDiffAsync(string diff) => Task.FromResult(Msg);
    public Task<IReadOnlyList<AffectedDoc>> FindAffectedDocsAsync(string impactSummary, IEnumerable<string> changedFiles, IEnumerable<DocCandidate> candidates)
        => Task.FromResult<IReadOnlyList<AffectedDoc>>([]);
}
