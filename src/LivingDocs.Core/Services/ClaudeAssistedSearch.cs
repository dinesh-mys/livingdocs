using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LivingDocs.Core.Interfaces;
using LivingDocs.Core.Models;
using LivingDocs.Core.Search;

namespace LivingDocs.Core.Services;

public sealed class ClaudeAssistedSearch : ISemanticSearchService
{
    // Haiku for reranking — fast and cheap, ~40 chunks per call.
    private const string RerankModel  = "claude-haiku-4-5-20251001";
    private const int    PreFilterK   = 40;   // BM25 candidates sent to Claude
    private const int    RerankAbove  = 10;   // only rerank when candidates exceed topK

    private readonly IClaudeService _claude;
    private readonly string         _indexPath;
    private readonly Bm25Index      _bm25 = new();
    private readonly List<DocChunk> _chunks = [];
    private bool _dirty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public ClaudeAssistedSearch(IClaudeService claude, string indexDir)
    {
        _claude    = claude;
        _indexPath = Path.Combine(indexDir, ".livingdocs", "chunks.json");
        Load();
    }

    // ── ISemanticSearchService ────────────────────────────────────────────

    public Task IndexAsync(DocChunk chunk)
    {
        // Replace any existing chunk at the same file + line position.
        var existing = _chunks.FirstOrDefault(
            c => c.FilePath == chunk.FilePath && c.LineNumber == chunk.LineNumber);

        if (existing is not null)
        {
            _chunks.Remove(existing);
            _bm25.Rebuild(_chunks);
        }

        _chunks.Add(chunk);
        _bm25.Add(chunk);
        _dirty = true;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK = 10)
    {
        var candidates = _bm25.Search(query, PreFilterK);

        if (candidates.Count == 0)
            return [];

        // BM25 result is already good enough when candidates ≤ topK.
        if (candidates.Count <= topK)
            return candidates
                .Select((c, rank) => new SearchResult
                {
                    Chunk = c.Chunk,
                    Score = 1.0f - rank / (float)Math.Max(candidates.Count, 1)
                })
                .ToList();

        return await RerankAsync(query, candidates.Select(c => c.Chunk).ToList(), topK);
    }

    public Task DeleteAsync(string filePath)
    {
        int before = _chunks.Count;
        _chunks.RemoveAll(c => c.FilePath == filePath);
        if (_chunks.Count != before)
        {
            _bm25.Rebuild(_chunks);
            _dirty = true;
        }
        return Task.CompletedTask;
    }

    public async Task FlushAsync()
    {
        if (!_dirty) return;
        var dir = Path.GetDirectoryName(_indexPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_indexPath, JsonSerializer.Serialize(_chunks, JsonOpts));
        _dirty = false;
    }

    public IndexStats GetStats() => new()
    {
        TotalChunks = _chunks.Count,
        LastIndexed = File.Exists(_indexPath)
            ? File.GetLastWriteTimeUtc(_indexPath)
            : null,
        Provider = "claude"
    };

    public async ValueTask DisposeAsync() => await FlushAsync();

    // ── Reranker ─────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query, IReadOnlyList<DocChunk> candidates, int topK)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c      = candidates[i];
            var symbol = c.ParentSymbol is not null ? $" ({c.ParentSymbol})" : string.Empty;
            var text   = c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content;
            sb.AppendLine($"[{i}] {c.FilePath}:{c.LineNumber}{symbol}");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        var prompt = $"""
            You are a code documentation search engine.
            Return the indices of the top {topK} most relevant documentation chunks for the
            question below. Output ONLY a JSON array of integers, nothing else.
            Example: [3, 0, 7, 2, 5]

            QUESTION: {query}

            CHUNKS:
            {sb}
            Return the JSON array:
            """;

        var response = await _claude.CompleteAsync(prompt, maxTokens: 100, model: RerankModel);
        var indices  = ParseIndices(response, candidates.Count, topK);

        // If Claude returns nothing usable, fall back to BM25 order.
        if (indices.Length == 0)
            indices = Enumerable.Range(0, Math.Min(topK, candidates.Count)).ToArray();

        return indices
            .Select((idx, rank) => new SearchResult
            {
                Chunk = candidates[idx],
                Score = 1.0f - rank / (float)topK
            })
            .ToList();
    }

    private static int[] ParseIndices(string response, int maxIdx, int topK)
    {
        try
        {
            var match = Regex.Match(response, @"\[[\d,\s]+\]");
            if (!match.Success) return [];

            return (JsonSerializer.Deserialize<int[]>(match.Value) ?? [])
                .Where(i => i >= 0 && i < maxIdx)
                .Distinct()
                .Take(topK)
                .ToArray();
        }
        catch { return []; }
    }

    // ── Persistence ───────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(_indexPath)) return;
        try
        {
            var chunks = JsonSerializer.Deserialize<List<DocChunk>>(
                File.ReadAllText(_indexPath), JsonOpts);
            if (chunks is null) return;
            _chunks.AddRange(chunks);
            _bm25.Rebuild(_chunks);
        }
        catch { /* corrupt index — start fresh */ }
    }
}
