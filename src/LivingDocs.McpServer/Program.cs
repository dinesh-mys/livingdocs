using LivingDocs.Core.Services;

LoadDotEnv();

if (args is ["scan", var repoPath])
{
    await RunScanAsync(repoPath);
}
else
{
    Console.WriteLine("LivingDocs — AI documentation health monitor");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  livingdocs scan <repo-path>   Detect stale doc comments");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  ANTHROPIC_API_KEY             Required for Claude suggestions");
}

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

    var result = await detector.DetectAsync(repoPath);

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
