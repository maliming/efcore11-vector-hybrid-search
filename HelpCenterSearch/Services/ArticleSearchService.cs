using HelpCenterSearch.Data;
using HelpCenterSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpCenterSearch.Services;

/// <summary>
///     The three retrieval strategies from the article, side by side on the same query.
/// </summary>
public class ArticleSearchService(HelpCenterDbContext db, IEmbeddingGenerator embeddings)
{
    private const int CandidateCount = 20;
    private const int RrfK = 60; // the constant from the original RRF paper

    private record Candidate(int Id, string Title, string Topic);

    /// <summary>Each retriever runs once; all three columns are derived from that pair of snapshots.</summary>
    public async Task<SearchResults> SearchAsync(string query, int take = 10)
    {
        var semantic = await SemanticCandidatesAsync(query);
        var lexical = await LexicalCandidatesAsync(query);

        return new SearchResults(
            ToHits(semantic, take, semanticSide: true),
            ToHits(lexical, take, semanticSide: false),
            Fuse(lexical, semantic, take),
            // The full candidate positions, so the page can show a rank the top ten cut off.
            Positions(lexical),
            Positions(semantic));
    }

    private static Dictionary<int, int> Positions(List<Candidate> candidates)
        => candidates.Select((c, i) => (c.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);

    /// <summary>Exact k-nearest-neighbor search, on stable API.</summary>
    private async Task<List<Candidate>> SemanticCandidatesAsync(string query)
    {
        var queryVector = embeddings.Generate(query);

        var rows = await db.Articles
            .OrderBy(a => EF.Functions.VectorDistance("cosine", a.Embedding, queryVector))
            // Positions decide the fused score, so ties must not be left to the server.
            .ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Title, a.Topic })
            .Take(CandidateCount)
            .ToListAsync();

        return [.. rows.Select(r => new Candidate(r.Id, r.Title, r.Topic))];
    }

    /// <summary>Keyword search through FREETEXTTABLE, which also returns SQL Server's rank.</summary>
    private async Task<List<Candidate>> LexicalCandidatesAsync(string query)
    {
        var rows = await db.Articles
            .FreeTextTable<Article, int>(query, topN: CandidateCount)
            .Join(db.Articles, fts => fts.Key, a => a.Id,
                (fts, a) => new { a.Id, a.Title, a.Topic, fts.Rank })
            .OrderByDescending(x => x.Rank)
            .ThenBy(x => x.Id)
            .ToListAsync();

        return [.. rows.Select(r => new Candidate(r.Id, r.Title, r.Topic))];
    }

    private static List<SearchHit> ToHits(List<Candidate> candidates, int take, bool semanticSide)
        =>
        [
            .. candidates
                .Take(take)
                .Select((c, i) => new SearchHit(
                    c.Id,
                    c.Title,
                    c.Topic,
                    semanticSide ? null : i + 1,
                    semanticSide ? i + 1 : null,
                    0))
        ];

    /// <summary>Both retrievers fused on rank positions, not on their raw scores.</summary>
    private static List<SearchHit> Fuse(List<Candidate> lexical, List<Candidate> semantic, int take)
    {
        var lexicalPosition = Positions(lexical);
        var semanticPosition = Positions(semantic);

        var meta = lexical
            .Concat(semantic)
            .DistinctBy(c => c.Id)
            .ToDictionary(c => c.Id);

        return
        [
            .. meta.Keys
                .Select(id => new SearchHit(
                    id,
                    meta[id].Title,
                    meta[id].Topic,
                    lexicalPosition.TryGetValue(id, out var l) ? l : null,
                    semanticPosition.TryGetValue(id, out var s) ? s : null,
                    (lexicalPosition.TryGetValue(id, out var lp) ? 1.0 / (RrfK + lp) : 0.0)
                    + (semanticPosition.TryGetValue(id, out var sp) ? 1.0 / (RrfK + sp) : 0.0)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Id)
                .Take(take)
        ];
    }

    /// <summary>
    ///     Two queries on purpose: vector columns are left out of the SELECT that materializes an
    ///     entity, so reading one back takes an explicit projection.
    /// </summary>
    public async Task<ArticleDetail?> GetArticleAsync(int id)
    {
        var article = await db.Articles.FirstOrDefaultAsync(a => a.Id == id);

        if (article == null)
        {
            return null;
        }

        var loadedWithEntity = db.Entry(article).Property(a => a.Embedding).IsLoaded;

        var embedding = await db.Articles
            .Where(a => a.Id == id)
            .Select(a => a.Embedding)
            .SingleAsync();

        var preview = embedding.Memory.Span[..Math.Min(8, embedding.Memory.Length)].ToArray();

        return new ArticleDetail(
            article.Id,
            article.Title,
            article.Topic,
            article.Content,
            loadedWithEntity,
            embedding.Memory.Length,
            preview);
    }
}
