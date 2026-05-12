namespace LivingDocs.Core.Interfaces;

public interface ILicenseService
{
    Task<LicenseStatus> GetStatusAsync();
}

public record LicenseStatus(bool IsValid, string Plan, string? Error);
