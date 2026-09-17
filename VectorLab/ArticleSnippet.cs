using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace VectorLab;

/// <summary>
///     The hybrid-search snippet exactly as it appears in the article, compiled here so the
///     published code is known to build against the same packages as the rest of the sample.
/// </summary>
public static class ArticleSnippet
{
    public static async Task<object> HybridSearchAsync(
        ProbeContext context,
        string queryText,
        SqlVector<float> queryVector)
    {
        const int candidateCount = 20;
        const int rrfK = 60; // the constant from the original RRF paper

        var lexical = await context.Articles
            .FreeTextTable<Article, int>(queryText, topN: candidateCount)
            .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { a.Id, a.Title, fts.Rank })
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.Id)
            .ToListAsync();

        var semantic = await context.Articles
            .VectorSearch(a => a.Embedding, queryVector, "cosine")
            .OrderBy(r => r.Distance)
            .Take(candidateCount)
            .WithApproximate()
            .Select(r => new { r.Value.Id, r.Value.Title, r.Distance })
            .ToListAsync();

        // VECTOR_SEARCH accepts a single ascending ordering on the distance, so the tie-break the
        // exact query does in SQL has to happen here instead.
        semantic = [.. semantic.OrderBy(x => x.Distance).ThenBy(x => x.Id)];

        var lexicalPosition = lexical.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);
        var semanticPosition = semantic.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);

        var titles = lexical.Select(x => (x.Id, x.Title))
            .Concat(semantic.Select(x => (x.Id, x.Title)))
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.Title);

        var fused = titles.Keys
            .Select(id => new
            {
                Title = titles[id],
                LexicalPosition = lexicalPosition.TryGetValue(id, out var l) ? l : (int?)null,
                SemanticPosition = semanticPosition.TryGetValue(id, out var s) ? s : (int?)null,
                Score = (lexicalPosition.TryGetValue(id, out var lp) ? 1.0 / (rrfK + lp) : 0.0)
                      + (semanticPosition.TryGetValue(id, out var sp) ? 1.0 / (rrfK + sp) : 0.0)
            })
            .OrderByDescending(x => x.Score)
            .Take(10)
            .ToList();

        return fused;
    }
}
