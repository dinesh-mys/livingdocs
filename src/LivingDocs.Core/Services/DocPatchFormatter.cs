using System.Text;

namespace LivingDocs.Core.Services;

/// <summary>Formats the difference between an existing doc comment and a suggested replacement as a unified diff patch (---/+++/@@ format), suitable for copy-paste or machine application via `git apply`.</summary>
public static class DocPatchFormatter
{
    /// <summary>Produces a unified diff hunk showing the original comment lines removed and the suggested lines added, anchored at the given line number in the file.</summary>
    public static string Format(string original, string suggested, string filePath, int lineNumber)
    {
        var oldLines = SplitLines(original);
        var newLines = SplitLines(suggested);

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{filePath}");
        sb.AppendLine($"+++ b/{filePath}");
        sb.AppendLine($"@@ -{lineNumber},{oldLines.Length} +{lineNumber},{newLines.Length} @@");

        foreach (var line in oldLines)
            sb.AppendLine($"-{line}");

        foreach (var line in newLines)
            sb.AppendLine($"+{line}");

        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
        => text.ReplaceLineEndings("\n").Split('\n');
}
