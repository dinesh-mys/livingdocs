using LivingDocs.Core.Interfaces;

internal static class LicenseGuard
{
    internal static async Task<string?> RequireProAsync(ILicenseService license)
    {
        var status = await license.GetStatusAsync();
        if (status.IsValid) return null;

        return status.Plan == "free"
            ? "This is a Pro feature. Get a license at https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM"
            : $"License error: {status.Error}";
    }
}
