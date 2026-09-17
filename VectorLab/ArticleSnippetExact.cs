using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace VectorLab;

/// <summary>
///     The fusion the article recommends: the semantic list comes from exact kNN, which runs on any
///     SQL Server 2025 instance, so the whole pipeline stays on non-experimental API.
/// </summary>
public static class ArticleSnippetExact
{
    public static async Task<IReadOnlyList<FusedRow>> HybridSearchAsync(
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
            .OrderBy(a => EF.Functions.VectorDistance("cosine", a.Embedding, queryVector))
            .ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Title })
            .Take(candidateCount)
            .ToListAsync();

        var lexicalPosition = lexical.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);
        var semanticPosition = semantic.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);

        var titles = lexical.Select(x => (x.Id, x.Title))
            .Concat(semantic.Select(x => (x.Id, x.Title)))
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.Title);

        return titles.Keys
            .Select(id => new FusedRow(
                id,
                titles[id],
                lexicalPosition.TryGetValue(id, out var l) ? l : null,
                semanticPosition.TryGetValue(id, out var s) ? s : null,
                (lexicalPosition.TryGetValue(id, out var lp) ? 1.0 / (rrfK + lp) : 0.0)
                + (semanticPosition.TryGetValue(id, out var sp) ? 1.0 / (rrfK + sp) : 0.0)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id)
            .Take(10)
            .ToList();
    }
}

public record FusedRow(int Id, string Title, int? LexicalPosition, int? SemanticPosition, double Score);
