using System.Net;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class TelemetryServiceTests : IDisposable
{
    private readonly Dictionary<string, string?> _original = new();
    private readonly string _tmpIdPath = Path.Combine(Path.GetTempPath(), $"ld-install-{Guid.NewGuid():N}");

    private void SetEnv(string key, string? value)
    {
        if (!_original.ContainsKey(key))
            _original[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        if (File.Exists(_tmpIdPath)) File.Delete(_tmpIdPath);
    }

    // Captures outgoing requests so we can assert payloads without a network.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<string> _bodies = new();
        private readonly object _lock = new();
        public bool Throw { get; set; }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_lock) return _bodies.ToList();
        }

        public void Clear()
        {
            lock (_lock) _bodies.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("boom");
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (_lock) _bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void IsEnabled_False_WhenDoNotTrackSet()
    {
        SetEnv("DO_NOT_TRACK", "1");
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void IsEnabled_False_WhenLivingDocsTelemetryOff()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", "off");
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void IsEnabled_True_ByDefault()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.True(svc.IsEnabled);
    }

    [Fact]
    public async Task TrackAsync_PostsEventWithInstallId()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler();
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);
        handler.Clear(); // drop the ctor's first_run

        await svc.TrackAsync("index_success", new Dictionary<string, string> { ["chunks"] = "10-50" });

        Assert.Single(handler.Snapshot());
        using var doc = JsonDocument.Parse(handler.Snapshot()[0]);
        var root = doc.RootElement;
        Assert.Equal("index_success", root.GetProperty("event").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("installId").GetString()));
        Assert.Equal("10-50", root.GetProperty("props").GetProperty("chunks").GetString());
    }

    [Fact]
    public async Task TrackAsync_NoOp_WhenOptedOut()
    {
        SetEnv("DO_NOT_TRACK", "1");
        var handler = new CapturingHandler();
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);
        handler.Clear();

        await svc.TrackAsync("mcp_started");

        Assert.Empty(handler.Snapshot());
    }

    [Fact]
    public async Task TrackAsync_DoesNotThrow_WhenEndpointFails()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler { Throw = true };
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);

        var ex = await Record.ExceptionAsync(() => svc.TrackAsync("mcp_started"));
        Assert.Null(ex);
    }

    [Fact]
    public void InstallId_CreatedOnce_ReusedAfter()
    {
        SetEnv("DO_NOT_TRACK", "1"); // disable network for this file-only test
        _ = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.True(File.Exists(_tmpIdPath));
        var first = File.ReadAllText(_tmpIdPath);
        _ = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.Equal(first, File.ReadAllText(_tmpIdPath));
    }

    [Fact]
    public async Task Track_FireAndForget_EventuallyPosts()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler();
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);
        handler.Clear(); // drop ctor first_run

        svc.Track("mcp_started");

        // Track is fire-and-forget; poll briefly for the background send to land.
        for (var i = 0; i < 50 && handler.Snapshot().Count == 0; i++)
            await Task.Delay(20);

        var bodies = handler.Snapshot();
        Assert.Single(bodies);
        using var doc = JsonDocument.Parse(bodies[0]);
        Assert.Equal("mcp_started", doc.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task Constructor_FirstRun_WritesNoticeAndPostsFirstRun()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler();
        var notice = new StringWriter();

        _ = new TelemetryService(new HttpClient(handler), _tmpIdPath, notice);

        // first_run is fire-and-forget; poll for it.
        for (var i = 0; i < 50 && handler.Snapshot().Count == 0; i++)
            await Task.Delay(20);

        Assert.Contains("DO_NOT_TRACK", notice.ToString());
        var bodies = handler.Snapshot();
        Assert.Single(bodies);
        using var doc = JsonDocument.Parse(bodies[0]);
        Assert.Equal("first_run", doc.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void Constructor_FirstRun_NoNotice_WhenOptedOut()
    {
        SetEnv("DO_NOT_TRACK", "1");
        var notice = new StringWriter();

        _ = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath, notice);

        Assert.Equal("", notice.ToString());
    }
}
