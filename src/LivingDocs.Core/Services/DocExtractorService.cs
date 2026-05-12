using System.Text.RegularExpressions;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Services;

public class DocExtractorService : IDocExtractorService
{
    public Task<IEnumerable<DocChunk>> ExtractAsync(string filePath, string content)
    {
        var language = DetectLanguage(filePath);
        var chunks = language switch
        {
            "csharp"     => ExtractCSharp(filePath, content),
            "typescript" => ExtractJsDoc(filePath, content, "typescript"),
            "javascript" => ExtractJsDoc(filePath, content, "javascript"),
            "python"     => ExtractPython(filePath, content),
            _            => Enumerable.Empty<DocChunk>()
        };

        return Task.FromResult(chunks);
    }

    // ── C# XML doc comments (//) ──────────────────────────────────────────

    private static IEnumerable<DocChunk> ExtractCSharp(string filePath, string content)
    {
        var chunks = new List<DocChunk>();
        var lines  = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("///")) continue;

            // Collect contiguous /// block
            var docLines = new List<string>();
            int start = i;
            while (i < lines.Length && lines[i].TrimStart().StartsWith("///"))
            {
                docLines.Add(lines[i].TrimStart().TrimStart('/').Trim());
                i++;
            }

            // The line after the block should be the symbol declaration
            var symbol = i < lines.Length ? ExtractCSharpSymbol(lines[i]) : null;

            chunks.Add(new DocChunk
            {
                FilePath      = filePath,
                Content       = string.Join(" ", docLines).Trim(),
                Language      = "csharp",
                LineNumber    = start + 1,
                ParentSymbol  = symbol,
                LastUpdated   = DateTime.UtcNow
            });
        }

        return chunks;
    }

    private static string? ExtractCSharpSymbol(string line)
    {
        // Match: public/private/protected + optional modifiers + type + name(
        var match = Regex.Match(line,
            @"(?:public|private|protected|internal|static|async|override|virtual)\s+.*?\s+(\w+)\s*[\(<]");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── JSDoc (/** ... */) for TypeScript / JavaScript ────────────────────

    private static IEnumerable<DocChunk> ExtractJsDoc(string filePath, string content, string language)
    {
        var chunks = new List<DocChunk>();
        var lines  = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("/**")) continue;

            var docLines = new List<string>();
            int start = i;

            // Single-line: /** ... */
            if (lines[i].Contains("*/"))
            {
                docLines.Add(CleanJsDocLine(lines[i]));
                i++;
            }
            else
            {
                i++; // skip /**
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("*/"))
                {
                    docLines.Add(CleanJsDocLine(lines[i]));
                    i++;
                }
                i++; // skip */
            }

            var symbol = i < lines.Length ? ExtractJsSymbol(lines[i]) : null;

            chunks.Add(new DocChunk
            {
                FilePath     = filePath,
                Content      = string.Join(" ", docLines).Trim(),
                Language     = language,
                LineNumber   = start + 1,
                ParentSymbol = symbol,
                LastUpdated  = DateTime.UtcNow
            });
        }

        return chunks;
    }

    private static string CleanJsDocLine(string line)
        => Regex.Replace(line.Trim().TrimStart('*').Trim(), @"^\*+\s?", "").Trim();

    private static string? ExtractJsSymbol(string line)
    {
        // function name() / const name = / class Name / async name(
        var match = Regex.Match(line,
            @"(?:export\s+)?(?:async\s+)?(?:function|class|const|let|var)\s+(\w+)");
        if (match.Success) return match.Groups[1].Value;

        // method shorthand: name(args) {
        match = Regex.Match(line, @"^\s*(\w+)\s*\(");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Python docstrings (\"\"\" ... \"\"\") ─────────────────────────────────────

    private static IEnumerable<DocChunk> ExtractPython(string filePath, string content)
    {
        var chunks = new List<DocChunk>();
        var lines  = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            // Detect def / class on previous meaningful line, or module-level docstring
            string? symbol = null;
            if (i > 0)
            {
                var prev = lines[i - 1].TrimStart();
                if (prev.StartsWith("def ") || prev.StartsWith("class "))
                    symbol = ExtractPythonSymbol(prev);
            }

            if (!trimmed.StartsWith("\"\"\"") && !trimmed.StartsWith("'''")) continue;

            var quote  = trimmed.StartsWith("\"\"\"") ? "\"\"\"" : "'''";
            var start  = i;
            var docLines = new List<string>();

            var firstLine = trimmed[3..]; // content after opening """

            if (firstLine.Contains(quote)) // single-line docstring
            {
                docLines.Add(firstLine[..firstLine.IndexOf(quote)].Trim());
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(firstLine)) docLines.Add(firstLine.Trim());
                i++;
                while (i < lines.Length && !lines[i].Contains(quote))
                {
                    docLines.Add(lines[i].Trim());
                    i++;
                }
                // capture content before closing """
                if (i < lines.Length)
                {
                    var closing = lines[i][..lines[i].IndexOf(quote)].Trim();
                    if (!string.IsNullOrWhiteSpace(closing)) docLines.Add(closing);
                }
            }

            var docContent = string.Join(" ", docLines.Where(l => !string.IsNullOrWhiteSpace(l))).Trim();
            if (string.IsNullOrWhiteSpace(docContent)) continue;

            chunks.Add(new DocChunk
            {
                FilePath     = filePath,
                Content      = docContent,
                Language     = "python",
                LineNumber   = start + 1,
                ParentSymbol = symbol,
                LastUpdated  = DateTime.UtcNow
            });
        }

        return chunks;
    }

    private static string? ExtractPythonSymbol(string line)
    {
        var match = Regex.Match(line, @"(?:def|class)\s+(\w+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Language detection ────────────────────────────────────────────────

    private static string DetectLanguage(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs"  => "csharp",
            ".ts"  => "typescript",
            ".tsx" => "typescript",
            ".js"  => "javascript",
            ".jsx" => "javascript",
            ".py"  => "python",
            _      => "unknown"
        };
}
