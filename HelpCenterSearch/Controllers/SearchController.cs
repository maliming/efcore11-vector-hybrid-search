using System.Diagnostics;
using HelpCenterSearch.Models;
using HelpCenterSearch.Services;
using Microsoft.AspNetCore.Mvc;

namespace HelpCenterSearch.Controllers;

public class SearchViewModel
{
    public string? Query { get; init; }
    public List<SearchHit> Semantic { get; init; } = [];
    public List<SearchHit> Keyword { get; init; } = [];
    public List<SearchHit> Hybrid { get; init; } = [];
    public Dictionary<int, int> KeywordPositions { get; init; } = [];
    public Dictionary<int, int> SemanticPositions { get; init; } = [];
}

public class SearchController(ArticleSearchService search) : Controller
{
    public async Task<IActionResult> Index(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return View(new SearchViewModel());
        }

        var results = await search.SearchAsync(query);

        return View(new SearchViewModel
        {
            Query = query,
            Semantic = results.Semantic,
            Keyword = results.Keyword,
            Hybrid = results.Hybrid,
            KeywordPositions = results.KeywordPositions,
            SemanticPositions = results.SemanticPositions
        });
    }

    /// <summary>The panel the page expands under the result lists. Returns a fragment, not a page.</summary>
    public async Task<IActionResult> Details(int id)
    {
        var article = await search.GetArticleAsync(id);

        return article == null
            ? NotFound()
            : PartialView("_ArticleDetail", article);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
