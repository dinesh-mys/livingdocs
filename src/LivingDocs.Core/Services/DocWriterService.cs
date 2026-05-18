using System.Text;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

/// <summary>Patches a doc comment block in a source file on disk. Detects the comment style and indentation from the original block, formats the suggestion in the same style, and splices the replacement lines into the file.</summary>
public class DocWriterService : IDocWriterService
{
    public async Task<string> WriteDocsAsync(
        string repoPath,
        string filePath,
        IReadOnlyList<(string Symbol, string Suggestion, float Confidence)> entries)
    {
        var docsDir = Path.Combine(repoPath, "docs");
        Directory.CreateDirectory(docsDir);

        var mdFileName = Path.GetFileNameWithoutExtension(filePath) + ".md";
        var mdPath     = Path.Combine(docsDir, mdFileName);
        var timestamp  = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC");

        var sb = new StringBuilder();

        if (!File.Exists(mdPath))
        {
            sb.AppendLine($"# {Path.GetFileName(filePath)} — LivingDocs Documentation");
            sb.AppendLine();
        }

        sb.AppendLine($"<!-- LivingDocs update: {timestamp} | {filePath} -->");
        sb.AppendLine($"## {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        foreach (var (symbol, suggestion, confidence) in entries)
        {
            sb.AppendLine($"### {symbol}");
            sb.AppendLine();
            sb.AppendLine(suggestion.Trim());
            sb.AppendLine();
            sb.AppendLine($"*Confidence: {confidence:P0}*");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();

        await File.AppendAllTextAsync(mdPath, sb.ToString());

        return Path.GetRelativePath(repoPath, mdPath);
    }

    public async Task<int> WriteBackAsync(
        string repoPath, string filePath, DocChunk chunk, string newCommentText)
    {
        var fullPath = Path.Combine(repoPath, filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Source file not found: {fullPath}");

        var lines     = (await File.ReadAllLinesAsync(fullPath)).ToList();
        var zeroIdx   = chunk.LineNumber - 1; // LineNumber is 1-based

        if (zeroIdx < 0 || zeroIdx >= lines.Count)
            throw new ArgumentOutOfRangeException(nameof(chunk),
                $"LineNumber {chunk.LineNumber} is out of range for {filePath} ({lines.Count} lines).");

        var (style, indent) = DetectStyle(lines[zeroIdx]);
        var blockEnd        = FindBlockEnd(lines, zeroIdx, style);
        var oldCount        = blockEnd - zeroIdx + 1;

        var newLines = FormatComment(newCommentText, style, indent);

        lines.RemoveRange(zeroIdx, oldCount);
        lines.InsertRange(zeroIdx, newLines);

        await File.WriteAllLinesAsync(fullPath, lines);
        return oldCount;
    }

    // ── Style detection ───────────────────────────────────────────────────

    private enum CommentStyle { CSharpXml, JsDoc, Python, Unknown }

    private static (CommentStyle style, string indent) DetectStyle(string firstLine)
    {
        var indent  = firstLine.Length - firstLine.TrimStart().Length;
        var prefix  = new string(' ', indent);
        var trimmed = firstLine.TrimStart();

        if (trimmed.StartsWith("///"))        return (CommentStyle.CSharpXml, prefix);
        if (trimmed.StartsWith("/**"))        return (CommentStyle.JsDoc,     prefix);
        if (trimmed.StartsWith("\"\"\""))     return (CommentStyle.Python,    prefix);
        if (trimmed.StartsWith("'''"))        return (CommentStyle.Python,    prefix);

        return (CommentStyle.Unknown, prefix);
    }

    // ── Block end detection ───────────────────────────────────────────────

    private static int FindBlockEnd(List<string> lines, int start, CommentStyle style)
    {
        int i = start;

        switch (style)
        {
            case CommentStyle.CSharpXml:
                while (i + 1 < lines.Count && lines[i + 1].TrimStart().StartsWith("///"))
                    i++;
                break;

            case CommentStyle.JsDoc:
                while (i < lines.Count && !lines[i].Contains("*/"))
                    i++;
                break;

            case CommentStyle.Python:
                var quote = lines[start].TrimStart().StartsWith("\"\"\"") ? "\"\"\"" : "'''";
                // If the opening line also closes (single-line docstring), stay at start.
                var afterOpen = lines[start].TrimStart()[3..];
                if (!afterOpen.Contains(quote))
                {
                    i++;
                    while (i < lines.Count && !lines[i].Contains(quote))
                        i++;
                }
                break;
        }

        return i;
    }

    // ── Comment formatting ────────────────────────────────────────────────

    private static List<string> FormatComment(string text, CommentStyle style, string indent)
    {
        // Strip any comment markers Claude may have included, then re-wrap cleanly.
        var raw = StripMarkers(text, style).Trim();

        return style switch
        {
            CommentStyle.CSharpXml => FormatCSharp(raw, indent),
            CommentStyle.JsDoc     => FormatJsDoc(raw, indent),
            CommentStyle.Python    => FormatPython(raw, indent),
            _                      => [indent + text.Trim()]
        };
    }

    private static List<string> FormatCSharp(string raw, string indent)
    {
        // Single-line summary → one line. Multi-line → <summary> block.
        var sentences = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0)
                           .ToList();

        if (sentences.Count == 1)
            return [$"{indent}/// <summary>{sentences[0]}</summary>"];

        var result = new List<string> { $"{indent}/// <summary>" };
        result.AddRange(sentences.Select(s => $"{indent}/// {s}"));
        result.Add($"{indent}/// </summary>");
        return result;
    }

    private static List<string> FormatJsDoc(string raw, string indent)
    {
        var sentences = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0)
                           .ToList();

        if (sentences.Count == 1)
            return [$"{indent}/** {sentences[0]} */"];

        var result = new List<string> { $"{indent}/**" };
        result.AddRange(sentences.Select(s => $"{indent} * {s}"));
        result.Add($"{indent} */");
        return result;
    }

    private static List<string> FormatPython(string raw, string indent)
    {
        var sentences = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0)
                           .ToList();

        if (sentences.Count == 1)
            return [$"{indent}\"\"\"{sentences[0]}\"\"\""];

        var result = new List<string> { $"{indent}\"\"\"" };
        result.AddRange(sentences.Select(s => $"{indent}{s}"));
        result.Add($"{indent}\"\"\"");
        return result;
    }

    // Strip comment markers that Claude may have included in its response.
    private static string StripMarkers(string text, CommentStyle style) => style switch
    {
        CommentStyle.CSharpXml => string.Join('\n',
            text.Split('\n').Select(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith("///") ? t[3..].TrimStart() : t;
            })),

        CommentStyle.JsDoc => string.Join('\n',
            text.Split('\n')
                .Select(l => l.TrimStart().TrimStart('*').TrimStart('/', '*').Trim())
                .Where(l => l != "/" && l != "**")),

        CommentStyle.Python => text
            .Replace("\"\"\"", string.Empty)
            .Replace("'''", string.Empty),

        _ => text
    };
}
