using System.ComponentModel;
using LivingDocs.Core.Interfaces;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class ProTools
{
    [McpServerTool(Name = "sync_confluence")]
    [Description(
        "PRO — Write updated documentation comments back to the matching Confluence page. " +
        "Requires LIVINGDOCS_LICENSE_KEY to be set.")]
    public static async Task<string> SyncConfluence(
        ILicenseService license,
        [Description("Absolute path to the git repository")] string repoPath,
        [Description("File path relative to the repository root, e.g. src/Tax.cs")] string filePath)
    {
        var error = await LicenseGuard.RequireProAsync(license);
        if (error is not null) return error;

        // TODO: implement Confluence sync
        // 1. Run suggest_doc_update to get the updated comment
        // 2. Find the matching Confluence page via Confluence REST API
        // 3. Patch the page body with the new content
        return $"[sync_confluence] Coming soon — will sync '{filePath}' in '{repoPath}' to Confluence.";
    }

    [McpServerTool(Name = "scan_org")]
    [Description(
        "PRO — Scan every repository in a GitHub organisation and return a staleness report " +
        "across all repos. Requires LIVINGDOCS_LICENSE_KEY to be set.")]
    public static async Task<string> ScanOrg(
        ILicenseService license,
        [Description("GitHub organisation name, e.g. my-company")] string orgName)
    {
        var error = await LicenseGuard.RequireProAsync(license);
        if (error is not null) return error;

        // TODO: implement org-wide scan
        // 1. List repos via GitHub API (GITHUB_TOKEN env var)
        // 2. Clone / fetch each repo
        // 3. Run StaleDocDetectorService on each
        // 4. Return aggregated staleness report
        return $"[scan_org] Coming soon — will scan all repos in '{orgName}'.";
    }
}
