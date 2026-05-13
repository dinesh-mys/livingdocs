using System.Text.RegularExpressions;
using LivingDocs.Core.Models;

namespace LivingDocs.Core.Search;

internal sealed class Bm25Index
{
    private const double K1 = 1.2;
    private const double B  = 0.75;

    private readonly List<DocChunk> _docs    = [];
    private readonly List<int>      _dlens   = [];  // token count per doc
    private readonly Dictionary<string, List<(int docIdx, int tf)>> _inverted = [];

    public int Count => _docs.Count;

    public void Add(DocChunk chunk)
    {
        int idx  = _docs.Count;
        var freq = Tokenize(chunk.Content);
        _docs.Add(chunk);
        _dlens.Add(freq.Values.Sum());

        foreach (var (term, tf) in freq)
        {
            if (!_inverted.TryGetValue(term, out var list))
                _inverted[term] = list = [];
            list.Add((idx, tf));
        }
    }

    // Full rebuild from a snapshot — used after deletions.
    public void Rebuild(IEnumerable<DocChunk> chunks)
    {
        _docs.Clear();
        _dlens.Clear();
        _inverted.Clear();
        foreach (var c in chunks) Add(c);
    }

    public IReadOnlyList<(DocChunk Chunk, double Score)> Search(string query, int topK)
    {
        if (_docs.Count == 0) return [];

        var queryTerms = Tokenize(query).Keys.ToList();
        var scores     = new double[_docs.Count];
        int N          = _docs.Count;
        double avgDl   = _dlens.Count > 0 ? _dlens.Average() : 1.0;

        foreach (var term in queryTerms)
        {
            if (!_inverted.TryGetValue(term, out var postings)) continue;

            double idf = Math.Log((N - postings.Count + 0.5) / (postings.Count + 0.5) + 1.0);

            foreach (var (docIdx, tf) in postings)
            {
                double dl     = _dlens[docIdx];
                double tfNorm = tf * (K1 + 1) / (tf + K1 * (1 - B + B * dl / avgDl));
                scores[docIdx] += idf * tfNorm;
            }
        }

        return scores
            .Select((s, i) => (Chunk: _docs[i], Score: s))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    private static Dictionary<string, int> Tokenize(string text)
        => Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
                .GroupBy(m => m.Value)
                .ToDictionary(g => g.Key, g => g.Count());
}
