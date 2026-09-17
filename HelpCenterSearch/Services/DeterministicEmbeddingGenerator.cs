using System.Security.Cryptography;
using System.Text;
using HelpCenterSearch.Models;
using Microsoft.Data.SqlTypes;

namespace HelpCenterSearch.Services;

/// <summary>
///     A stand-in so the sample runs with no API key: it drops text into one of four topic clusters,
///     which is enough to show semantic retrieval but is not a real embedding model. For a real one,
///     call <c>Microsoft.Extensions.AI</c> from an implementation of this interface.
/// </summary>
public class DeterministicEmbeddingGenerator : IEmbeddingGenerator
{
    private static readonly string[][] TopicVocabularies =
    [
        ["login", "session", "password", "sign", "timeout", "expire", "token", "authentic"],
        ["invoice", "billing", "payment", "subscription", "refund", "plan", "card", "renew"],
        ["export", "import", "csv", "report", "download", "schedule", "template", "column"],
        ["api", "webhook", "rate", "limit", "endpoint", "retry", "payload", "signature"]
    ];

    private static readonly float[][] Centroids = BuildCentroids();

    public SqlVector<float> Generate(string text)
    {
        var weights = new double[TopicVocabularies.Length];

        foreach (var word in Tokenize(text))
        {
            for (var t = 0; t < TopicVocabularies.Length; t++)
            {
                if (TopicVocabularies[t].Any(v => word.Contains(v, StringComparison.Ordinal)))
                {
                    weights[t]++;
                }
            }
        }

        if (weights.All(w => w == 0))
        {
            // Nothing recognisable: pick a stable direction from the text itself.
            var hash = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
            weights[(uint)hash % (uint)TopicVocabularies.Length] = 1;
        }

        var vector = new float[Article.EmbeddingDimensions];
        for (var t = 0; t < Centroids.Length; t++)
        {
            if (weights[t] == 0)
            {
                continue;
            }

            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] += (float)(Centroids[t][i] * weights[t]);
            }
        }

        return new SqlVector<float>(Normalize(vector));
    }

    private static IEnumerable<string> Tokenize(string text)
        => text.ToLowerInvariant().Split(
            [' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')', '-'],
            StringSplitOptions.RemoveEmptyEntries);

    private static float[][] BuildCentroids()
    {
        var centroids = new float[TopicVocabularies.Length][];
        for (var t = 0; t < centroids.Length; t++)
        {
            var random = new Random(9000 + t);
            var values = new float[Article.EmbeddingDimensions];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)(random.NextDouble() * 2 - 1);
            }

            centroids[t] = Normalize(values);
        }

        return centroids;
    }

    private static float[] Normalize(float[] values)
    {
        var norm = MathF.Sqrt(values.Sum(v => v * v));
        if (norm == 0)
        {
            return values;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }

        return values;
    }
}
