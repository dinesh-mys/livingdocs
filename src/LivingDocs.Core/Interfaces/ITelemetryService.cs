namespace LivingDocs.Core.Interfaces;

/// <summary>
/// Sends anonymous funnel telemetry. All methods are safe to call from any
/// context — they never throw and never write to stdout/stderr.
/// </summary>
public interface ITelemetryService
{
    /// <summary>Fire-and-forget. Returns immediately; the send happens in the background.</summary>
    void Track(string @event, IReadOnlyDictionary<string, string>? props = null);

    /// <summary>Awaitable variant for short-lived (CLI) processes that exit before a fire-and-forget send would complete.</summary>
    Task TrackAsync(string @event, IReadOnlyDictionary<string, string>? props = null);
}
