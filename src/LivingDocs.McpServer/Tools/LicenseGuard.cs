using LivingDocs.Core.Interfaces;

internal static class LicenseGuard
{
    internal static async Task<string?> RequireProAsync(ILicenseService license)
    {
        var status = await license.GetStatusAsync();
        if (status.IsValid) return null;

        return status.Plan == "free"
            ? """
              ⚡ Pro feature — try free for 7 days, then $10/month.

              What you get with Pro:
              • write_docs  — write Claude-generated docs to .md files with timestamps
              • scan_org    — scan every repo in your GitHub org at once
              • sync_confluence — push updated docs to Confluence automatically

              Start free trial: https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM

              Set LIVINGDOCS_LICENSE_KEY=<your-key> and you're done.
              """
            : $"License error: {status.Error}";
    }
}
