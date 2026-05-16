using LivingDocs.Core.Interfaces;

internal static class LicenseGuard
{
    internal static async Task<string?> RequireProAsync(ILicenseService license)
    {
        var status = await license.GetStatusAsync();
        if (status.IsValid) return null;

        return status.Plan == "free"
            ? """
              ⚡ Pro feature — $10/month unlocks this.

              What you get with Pro:
              • scan_org  — scan every repo in your GitHub org at once
              • sync_confluence — push updated docs to Confluence automatically

              Activate in < 2 minutes: https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM

              Set LIVINGDOCS_LICENSE_KEY=<your-key> and you're done.
              """
            : $"License error: {status.Error}";
    }
}
