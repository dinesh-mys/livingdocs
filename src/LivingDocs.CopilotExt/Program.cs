var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<GitHubDiffService>();

var app = builder.Build();

app.MapPost("/api/copilot/chat", CopilotChatEndpoint.HandleAsync);

app.Run();
