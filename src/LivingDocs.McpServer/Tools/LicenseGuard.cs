using LivingDocs.Core.Interfaces;

internal static class LicenseGuard
{
    internal static async Task<string?> RequireProAsync(ILicenseService license, ITelemetryService telemetry)
    {
        var status = await license.GetStatusAsync();
        if (status.IsValid) return null;

        if (status.Plan == "free")
        {
            telemetry.Track("upsell_shown", new Dictionary<string, string> { ["source"] = "pro_tool" });
            return """
              ⚡ Pro feature — $10/month, free for 7 days. No credit card needed to start.

              Connect your team's knowledge wherever it lives:
              • index_slack      — index Slack channels and threads
              • index_teams      — index Microsoft Teams conversations
              • index_email      — index email for institutional knowledge
              • write_docs       — write AI-generated docs to .md files with timestamps
              • scan_org         — scan every repo in your GitHub org at once
              • sync_confluence  — push updated docs to Confluence automatically

              Start your free trial → https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM

              Once you have your key: set LIVINGDOCS_LICENSE_KEY=<your-key>
              """;
        }

        return $"License error: {status.Error}";
    }
}
