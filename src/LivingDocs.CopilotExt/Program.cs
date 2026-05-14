using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Services;
using LivingDocs.Core.Models;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<GitHubDiffService>();

builder.Services
    .AddSingleton<IClaudeService>(_ =>
    {
        try   { return new ClaudeService(new HttpClient()); }
        catch  { return new NullClaudeService(); }
    })
    .AddSingleton<IDocExtractorService, DocExtractorService>()
    .AddSingleton<IGitScannerService, GitScannerService>()
    .AddSingleton<IStaleDocDetectorService, StaleDocDetectorService>()
    .AddSingleton<ISemanticSearchServiceFactory, ClaudeAssistedSearchFactory>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("ok"));
app.MapPost("/api/copilot/chat", CopilotChatEndpoint.HandleAsync);
app.MapPost("/api/github/webhook", GitHubWebhookHandler.HandleAsync);

app.Run();

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
