using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

/// <summary>
/// Sends anonymous funnel telemetry to the LivingDocs web endpoint.
/// Fire-and-forget, never throws, 2-second timeout, and never writes to
/// stdout/stderr (so it is safe inside the MCP stdio JSON-RPC stream).
/// Disabled when DO_NOT_TRACK=1 or LIVINGDOCS_TELEMETRY=off.
/// </summary>
public sealed class TelemetryService : ITelemetryService
{
    private const string EndpointUrl = "https://livingdocs-web.vercel.app/api/event";

    private readonly HttpClient _http;
    private readonly string _installId;
    private readonly string _version;
    private readonly string _os;

    /// <summary>True when telemetry is enabled (no opt-out env var set).</summary>
    public bool IsEnabled { get; }

    /// <param name="http">HTTP client used for the POST. Each send is bounded by a 2s timeout.</param>
    /// <param name="installIdPath">Override for the install-id file path (tests). Defaults to ~/.livingdocs/install-id.</param>
    /// <param name="noticeWriter">Optional writer for the first-run notice (CLI only; never pass in MCP stdio mode).</param>
    public TelemetryService(HttpClient http, string? installIdPath = null, TextWriter? noticeWriter = null)
    {
        _http = http;
        IsEnabled = !IsOptedOut();
        _version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        _os = GetOs();

        var (id, isNew) = EnsureInstallId(installIdPath);
        _installId = id;

        if (isNew && IsEnabled)
        {
            noticeWriter?.WriteLine(
                "ℹ Anonymous usage stats are on (no code or paths collected). Disable with DO_NOT_TRACK=1.");
            Track("first_run");
        }
    }

    /// <inheritdoc />
    public void Track(string @event, IReadOnlyDictionary<string, string>? props = null)
        => _ = TrackAsync(@event, props);

    /// <inheritdoc />
    public async Task TrackAsync(string @event, IReadOnlyDictionary<string, string>? props = null)
    {
        if (!IsEnabled) return;
        try
        {
            // System.Text.Json serializes the `@event` member as the JSON field "event".
            var payload = new
            {
                @event,
                installId = _installId,
                version = _version,
                os = _os,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                props = props ?? new Dictionary<string, string>(),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _http.PostAsJsonAsync(EndpointUrl, payload, cts.Token);
        }
        catch
        {
            // Telemetry must never affect the tool.
        }
    }

    private static bool IsOptedOut()
    {
        var dnt = Environment.GetEnvironmentVariable("DO_NOT_TRACK");
        if (dnt == "1" || string.Equals(dnt, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(
            Environment.GetEnvironmentVariable("LIVINGDOCS_TELEMETRY"), "off",
            StringComparison.OrdinalIgnoreCase);
    }

    private static (string id, bool isNew) EnsureInstallId(string? overridePath)
    {
        try
        {
            string file;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                file = overridePath;
            }
            else
            {
                file = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".livingdocs",
                    "install-id");
            }

            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                    return (existing, false);
            }

            var dirName = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return (id, true);
        }
        catch
        {
            // Unwritable home dir — process-scoped id, suppress first_run.
            return (Guid.NewGuid().ToString("N"), false);
        }
    }

    private static string GetOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }
}
