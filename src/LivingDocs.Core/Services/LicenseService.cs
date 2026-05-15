using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

public sealed class LicenseService : ILicenseService
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly string? _orgId;
    private readonly string? _benefitId;
    private readonly string? _accessToken;
    private LicenseStatus? _cache;

    private const string StoreUrl    = "https://polar.sh/novaders-llp/livingdocs";
    private const string ValidateUrl = "https://api.polar.sh/v1/license-keys/validate";

    public LicenseService(HttpClient http)
    {
        _http         = http;
        _key          = Environment.GetEnvironmentVariable("LIVINGDOCS_LICENSE_KEY");
        _orgId        = Environment.GetEnvironmentVariable("POLAR_ORGANIZATION_ID");
        _benefitId    = Environment.GetEnvironmentVariable("POLAR_BENEFIT_ID");
        _accessToken  = Environment.GetEnvironmentVariable("POLAR_ACCESS_TOKEN");
    }

    public async Task<LicenseStatus> GetStatusAsync()
    {
        if (_cache is not null) return _cache;

        if (string.IsNullOrWhiteSpace(_key))
        {
            _cache = new LicenseStatus(false, "free", null);
            return _cache;
        }

        _cache = string.IsNullOrWhiteSpace(_orgId)
            ? FormatCheck()
            : await ValidateWithPolarAsync();

        return _cache;
    }

    private LicenseStatus FormatCheck()
    {
        var valid = _key!.StartsWith("LD-", StringComparison.OrdinalIgnoreCase) && _key.Length >= 15;
        return valid
            ? new LicenseStatus(true, "pro", null)
            : new LicenseStatus(false, "invalid", $"Invalid license key. Get yours at {StoreUrl}");
    }

    private async Task<LicenseStatus> ValidateWithPolarAsync()
    {
        try
        {
            object payload = string.IsNullOrWhiteSpace(_benefitId)
                ? new { key = _key, organization_id = _orgId }
                : new { key = _key, organization_id = _orgId, benefit_id = _benefitId };

            var request = new HttpRequestMessage(HttpMethod.Post, ValidateUrl);
            request.Content = JsonContent.Create(payload);
            if (!string.IsNullOrWhiteSpace(_accessToken))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new LicenseStatus(false, "invalid",
                    $"License key not found. Get yours at {StoreUrl}");

            if (!response.IsSuccessStatusCode)
                return new LicenseStatus(false, "error",
                    $"License validation failed (HTTP {(int)response.StatusCode}). Check your LIVINGDOCS_LICENSE_KEY.");

            var body = await response.Content.ReadFromJsonAsync<PolarValidateResponse>();

            if (body?.Status == "granted")
            {
                if (body.ExpiresAt.HasValue && body.ExpiresAt.Value < DateTime.UtcNow)
                    return new LicenseStatus(false, "expired",
                        $"License expired on {body.ExpiresAt.Value:yyyy-MM-dd}. Renew at {StoreUrl}");

                return new LicenseStatus(true, "pro", null);
            }

            return new LicenseStatus(false, body?.Status ?? "invalid",
                $"License is {body?.Status ?? "invalid"}. Renew at {StoreUrl}");
        }
        catch (HttpRequestException ex)
        {
            return new LicenseStatus(false, "error",
                $"Could not reach license server: {ex.Message}");
        }
    }

    private sealed record PolarValidateResponse(
        [property: JsonPropertyName("status")]     string?   Status,
        [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt);
}
