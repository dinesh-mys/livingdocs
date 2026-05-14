var app = WebApplication.Create(args);
app.MapGet("/health", () => "ok");
app.MapGet("/", () => "LivingDocs running");
app.Run();
