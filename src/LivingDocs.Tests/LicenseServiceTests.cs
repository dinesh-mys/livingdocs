using System.Net;
using System.Net.Http.Json;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class LicenseServiceTests : IDisposable
{
    // Saves and restores env vars for test isolation
    private readonly Dictionary<string, string?> _original = new();

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
    }

    // ── Format-only path (no POLAR_ORGANIZATION_ID) ─────────────────────────

    [Fact]
    public async Task GetStatusAsync_NoKey_ReturnsFree()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", null);
        SetEnv("POLAR_ORGANIZATION_ID", null);

        var svc = new LicenseService(new HttpClient());
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("free", status.Plan);
    }

    [Fact]
    public async Task GetStatusAsync_ValidFormatKey_NoOrgId_ReturnsPro()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-1234-5678-ABCD");
        SetEnv("POLAR_ORGANIZATION_ID", null);

        var svc = new LicenseService(new HttpClient());
        var status = await svc.GetStatusAsync();

        Assert.True(status.IsValid);
        Assert.Equal("pro", status.Plan);
    }

    [Fact]
    public async Task GetStatusAsync_InvalidFormatKey_NoOrgId_ReturnsInvalid()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "BADKEY");
        SetEnv("POLAR_ORGANIZATION_ID", null);

        var svc = new LicenseService(new HttpClient());
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("invalid", status.Plan);
        Assert.Contains("polar.sh", status.Error);
    }

    // ── Polar.sh validation path ─────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_PolarGranted_ReturnsPro()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-TEST-KEY-1234");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");
        SetEnv("POLAR_BENEFIT_ID", null);

        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"status":"granted","expires_at":null}""");

        var svc = new LicenseService(new HttpClient(handler));
        var status = await svc.GetStatusAsync();

        Assert.True(status.IsValid);
        Assert.Equal("pro", status.Plan);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_PolarRevoked_ReturnsInvalid()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-TEST-KEY-1234");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");

        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"status":"revoked","expires_at":null}""");

        var svc = new LicenseService(new HttpClient(handler));
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("revoked", status.Plan);
        Assert.Contains("polar.sh", status.Error);
    }

    [Fact]
    public async Task GetStatusAsync_PolarNotFound_ReturnsInvalid()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-NOTFOUND-KEY");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");

        var handler = new FakeHttpHandler(HttpStatusCode.NotFound, "");

        var svc = new LicenseService(new HttpClient(handler));
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("invalid", status.Plan);
        Assert.Contains("not found", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatusAsync_PolarGrantedExpired_ReturnsExpired()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-TEST-KEY-1234");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");

        var pastDate = DateTime.UtcNow.AddDays(-1).ToString("o");
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            $$$"""{"status":"granted","expires_at":"{{{pastDate}}}"}""");

        var svc = new LicenseService(new HttpClient(handler));
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("expired", status.Plan);
        Assert.Contains("expired", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatusAsync_PolarNetworkError_ReturnsError()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-TEST-KEY-1234");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");

        var svc = new LicenseService(new HttpClient(new ThrowingHandler()));
        var status = await svc.GetStatusAsync();

        Assert.False(status.IsValid);
        Assert.Equal("error", status.Plan);
        Assert.Contains("license server", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatusAsync_CachesResult()
    {
        SetEnv("LIVINGDOCS_LICENSE_KEY", "LD-TEST-KEY-1234");
        SetEnv("POLAR_ORGANIZATION_ID", "org-uuid-123");

        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"status":"granted","expires_at":null}""");

        var svc = new LicenseService(new HttpClient(handler));

        await svc.GetStatusAsync();
        await svc.GetStatusAsync();

        Assert.Equal(1, handler.CallCount); // second call should hit cache
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class FakeHttpHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
