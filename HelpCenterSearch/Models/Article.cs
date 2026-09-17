using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.Data.SqlTypes;

namespace HelpCenterSearch.Models;

/// <summary>The embedding is derived from the title and the body: regenerate it when either changes.</summary>
public class Article
{
    public const int EmbeddingDimensions = 1536;

    public int Id { get; set; }

    public string Title { get; set; } = null!;

    public string Content { get; set; } = null!;

    public string Topic { get; set; } = null!;

    [Column(TypeName = "vector(1536)")]
    public SqlVector<float> Embedding { get; set; }
}

public record SearchHit(
    int Id,
    string Title,
    string Topic,
    int? LexicalPosition,
    int? SemanticPosition,
    double Score);

public record SearchResults(
    List<SearchHit> Semantic,
    List<SearchHit> Keyword,
    List<SearchHit> Hybrid,
    Dictionary<int, int> KeywordPositions,
    Dictionary<int, int> SemanticPositions);

public record ArticleDetail(
    int Id,
    string Title,
    string Topic,
    string Content,
    bool EmbeddingLoadedWithEntity,
    int EmbeddingDimensions,
    IReadOnlyList<float> EmbeddingPreview);
