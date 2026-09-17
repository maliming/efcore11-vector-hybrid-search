using Microsoft.Data.SqlTypes;

namespace HelpCenterSearch.Services;

/// <summary>Documents and queries must use the same generator: distances only mean something within one model.</summary>
public interface IEmbeddingGenerator
{
    SqlVector<float> Generate(string text);
}
