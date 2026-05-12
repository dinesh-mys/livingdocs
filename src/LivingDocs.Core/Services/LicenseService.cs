using System.Net.Http.Json;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

public sealed class LicenseService : ILicenseService
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private LicenseStatus? _cache;

    public LicenseService(HttpClient http)
    {
        _http = http;
        _key = Environment.GetEnvironmentVariable("LIVINGDOCS_LICENSE_KEY");
    }

    public async Task<LicenseStatus> GetStatusAsync()
    {
        if (_cache is not null) return _cache;

        if (string.IsNullOrWhiteSpace(_key))
        {
            _cache = new LicenseStatus(false, "free", null);
            return _cache;
        }

        // TODO: replace stub with real Polar.sh validation once org is live:
        //
        //   POST https://api.polar.sh/v1/users/licenses/validate
        //   { "key": _key, "organization_name": "dinesh-mys", "benefit_id": "<polar-benefit-id>" }
        //
        // var response = await _http.PostAsJsonAsync(
        //     "https://api.polar.sh/v1/users/licenses/validate",
        //     new { key = _key, organization_name = "dinesh-mys" });
        // var body = await response.Content.ReadFromJsonAsync<PolarValidateResponse>();
        // _cache = body?.Valid == true
        //     ? new LicenseStatus(true, "pro", null)
        //     : new LicenseStatus(false, "invalid", body?.Reason ?? "License check failed.");

        // Stub: accept any key in format LD-XXXX-XXXX-XXXX (16+ chars after prefix)
        var valid = _key.StartsWith("LD-", StringComparison.OrdinalIgnoreCase) && _key.Length >= 15;
        _cache = valid
            ? new LicenseStatus(true, "pro", null)
            : new LicenseStatus(false, "invalid",
                $"Invalid license key '{_key}'. Get yours at https://polar.sh/dinesh-mys/livingdocs");

        return _cache;
    }
}
