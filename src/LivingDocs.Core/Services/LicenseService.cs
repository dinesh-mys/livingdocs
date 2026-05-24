using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

/// <summary>
/// Validates LivingDocs license keys against the Polar licensing API (Pro)
/// or via offline RSA-signed JWT verification (Enterprise).
/// Results are cached in-process for the lifetime of the service instance.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    private readonly HttpClient _http;
    private readonly string? _key;
    private readonly string? _orgId;
    private readonly string? _benefitId;
    private readonly string? _accessToken;
    private LicenseStatus? _cache;

    private const string StoreUrl    = "https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM";
    private const string ValidateUrl = "https://api.polar.sh/v1/license-keys/validate";
    private const string OrgId       = "84ccc684-1529-459e-905b-317c1abbcf76";

    // RSA public key for offline enterprise JWT validation (private key held by Novaders LLP)
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAx0gqKoNnFtulJsn7VP9x
        iNcXei/qxiWj2n2qZTfWkE9HBP+RHO3jJWJ7eAQCfkVp/sFGG5ECjJ9HBJwmfDUI
        GwSr9m2D7iIYK55Rj9FT6VVyZd4YqxW4jPXxTSqiGMl6Fl6qLtrfcsQLdk4/+x7S
        qb0SkB0zt0MFPPyZPulLtUV/MtSHItXtRYT1mHBxmNUoJ1w/W7kFNgp34Re5/PeZ
        8+Xx0UujARNON4xjV7jCCt/R6cqZ68A7ThLvk7yM7FG0Cv2No+ZHIg4CkVGyw/qL
        ZmZcODvUNoyUBkFpagMsHDvUfq5pK2RUPOaLQ5/XM6K7fZ8VbxGm7m0ha5H/ASro
        cwIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// Initializes the service, reading license configuration from environment variables:
    /// <c>LIVINGDOCS_LICENSE_KEY</c>, <c>POLAR_ORGANIZATION_ID</c>,
    /// <c>POLAR_BENEFIT_ID</c>, and <c>POLAR_ACCESS_TOKEN</c>.
    /// </summary>
    /// <param name="http">HTTP client used for Polar API validation calls.</param>
    public LicenseService(HttpClient http)
    {
        _http        = http;
        _key         = Environment.GetEnvironmentVariable("LIVINGDOCS_LICENSE_KEY");
        _orgId       = Environment.GetEnvironmentVariable("POLAR_ORGANIZATION_ID");
        _benefitId   = Environment.GetEnvironmentVariable("POLAR_BENEFIT_ID");
        _accessToken = Environment.GetEnvironmentVariable("POLAR_ACCESS_TOKEN");
    }

    /// <summary>
    /// Returns the current license status, using a cached result on subsequent calls.
    /// Detects JWT-format keys for offline Enterprise validation; falls back to Polar API for Pro keys.
    /// </summary>
    /// <returns>
    /// A <see cref="LicenseStatus"/> indicating whether the license is valid,
    /// the tier (<c>free</c>, <c>pro</c>, or <c>enterprise</c>), and any error message.
    /// </returns>
    public async Task<LicenseStatus> GetStatusAsync()
    {
        if (_cache is not null) return _cache;

        if (string.IsNullOrWhiteSpace(_key))
        {
            _cache = new LicenseStatus(false, "free", null);
            return _cache;
        }

        // Enterprise offline JWT — no internet required
        if (IsJwt(_key))
        {
            _cache = ValidateJwtLicense();
            return _cache;
        }

        // Pro key — always validate with Polar to enforce trial expiry
        _cache = await ValidateWithPolarAsync();

        return _cache;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="key"/> looks like a JWT
    /// (three Base64Url segments separated by dots, starting with <c>eyJ</c>).
    /// </summary>
    private static bool IsJwt(string key)
    {
        var parts = key.Split('.');
        return parts.Length == 3 && key.StartsWith("eyJ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates an Enterprise JWT license offline using RSA-SHA256 signature verification.
    /// Checks the algorithm header, signature integrity, issuer (<c>livingdocs</c>), and expiry claim.
    /// </summary>
    /// <returns>A valid <see cref="LicenseStatus"/> with the tier from the JWT payload, or an error status.</returns>
    private LicenseStatus ValidateJwtLicense()
    {
        try
        {
            var parts = _key!.Split('.');
            if (parts.Length != 3)
                return new LicenseStatus(false, "invalid", "Invalid enterprise license format.");

            // Verify header declares RS256
            var header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
            if (header.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")
                return new LicenseStatus(false, "invalid", "Unsupported license algorithm.");

            // Verify RSA-SHA256 signature with embedded public key
            var dataToSign = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
            var signature  = Base64UrlDecodeBytes(parts[2]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);

            if (!rsa.VerifyData(dataToSign, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new LicenseStatus(false, "invalid",
                    "Enterprise license signature is invalid. Contact support@novaders.com");

            var payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;

            // Check issuer
            if (payload.TryGetProperty("iss", out var iss) &&
                iss.GetString() != "livingdocs")
                return new LicenseStatus(false, "invalid", "License issuer mismatch.");

            // Check expiry
            if (payload.TryGetProperty("exp", out var expProp))
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(expProp.GetInt64());
                if (exp < DateTimeOffset.UtcNow)
                    return new LicenseStatus(false, "expired",
                        $"Enterprise license expired on {exp:yyyy-MM-dd}. Contact support@novaders.com to renew.");
            }

            var tier = payload.TryGetProperty("tier", out var tierProp)
                ? tierProp.GetString() ?? "enterprise"
                : "enterprise";

            return new LicenseStatus(true, tier, null);
        }
        catch (Exception ex)
        {
            return new LicenseStatus(false, "invalid", $"License validation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs a basic structural check on a Pro license key — must start with <c>LD-</c>
    /// and be at least 15 characters. Does not contact the Polar API.
    /// </summary>
    private LicenseStatus FormatCheck()
    {
        var valid = _key!.StartsWith("LD-", StringComparison.OrdinalIgnoreCase) && _key.Length >= 15;
        return valid
            ? new LicenseStatus(true, "pro", null)
            : new LicenseStatus(false, "invalid", $"Invalid license key. Get yours at {StoreUrl}");
    }

    /// <summary>
    /// Validates a Pro license key against the Polar API (<c>/v1/license-keys/validate</c>).
    /// Always called for Pro keys to enforce trial expiry. Returns an error status on network failure
    /// rather than failing open.
    /// </summary>
    private async Task<LicenseStatus> ValidateWithPolarAsync()
    {
        try
        {
            var orgId = string.IsNullOrWhiteSpace(_orgId) ? OrgId : _orgId;
            object payload = string.IsNullOrWhiteSpace(_benefitId)
                ? new { key = _key, organization_id = orgId }
                : new { key = _key, organization_id = orgId, benefit_id = _benefitId };

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

    /// <summary>Decodes a Base64Url-encoded string to a byte array.</summary>
    private static byte[] Base64UrlDecode(string input) =>
        Convert.FromBase64String(Pad(input.Replace('-', '+').Replace('_', '/')));

    /// <summary>Alias for <see cref="Base64UrlDecode"/> used when the intent is raw bytes (e.g. signature).</summary>
    private static byte[] Base64UrlDecodeBytes(string input) =>
        Base64UrlDecode(input);

    /// <summary>Adds standard Base64 padding characters to a Base64Url-encoded string.</summary>
    private static string Pad(string s) =>
        s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');

    /// <summary>Minimal projection of the Polar <c>/v1/license-keys/validate</c> response body.</summary>
    private sealed record PolarValidateResponse(
        [property: JsonPropertyName("status")]     string?   Status,
        [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt);
}
