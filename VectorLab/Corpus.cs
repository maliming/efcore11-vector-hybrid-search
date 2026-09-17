namespace VectorLab;

public enum Recall
{
    /// <summary>Matches the query on both the text and the vector side.</summary>
    Both,

    /// <summary>Contains the query words but is semantically unrelated — a lexical false friend.</summary>
    LexicalOnly,

    /// <summary>Semantically on topic but shares no words with the query.</summary>
    SemanticOnly,

    /// <summary>Matches on neither side.</summary>
    Neither
}

public sealed record Document(int Id, string Title, string Content, string Topic, Recall Recall, float[] Embedding);

/// <summary>
///     A deliberately adversarial corpus for evaluating hybrid search.
///     The query is the text "nebula quasar" plus the astronomy topic vector. Documents are built
///     so that each recall category is populated, which is what makes a fusion function's ranking
///     behaviour observable. Embeddings are deterministic topic centroids plus noise, so the sample
///     runs without an embedding service.
/// </summary>
public static class Corpus
{
    public const int Dimensions = 8;

    public const string QueryText = "nebula quasar";
    public const string QueryTopic = "astronomy";

    private static readonly string[] Topics = ["astronomy", "databases", "aviation", "cooking"];

    /// <summary>Astronomy words that appear in the query.</summary>
    private static readonly string[] QueryWords = ["nebula", "quasar"];

    /// <summary>Astronomy words that do not appear in the query.</summary>
    private static readonly string[] AstronomyOther =
        ["parallax", "redshift", "corona", "pulsar", "ecliptic", "albedo", "perihelion", "magnitude"];

    private static readonly string[] DatabaseWords =
        ["index", "query", "transaction", "replication", "schema", "latency", "shard", "rollback"];

    private static readonly string[] AviationWords =
        ["airfoil", "runway", "altitude", "turbine", "fuselage", "airspeed", "rudder", "glideslope"];

    private static readonly string[] CookingWords =
        ["braise", "simmer", "marinade", "pastry", "skillet", "sourdough", "julienne", "emulsion"];

    private static readonly float[][] Centroids = BuildCentroids();

    public static IReadOnlyList<Document> Build()
    {
        var documents = new List<Document>();
        var id = 0;

        // Both: astronomy prose that uses the query words.
        for (var i = 0; i < 12; i++)
        {
            id++;
            documents.Add(new Document(
                id,
                $"Deep sky survey {id}",
                Sentence(id, Mix(QueryWords, AstronomyOther)),
                "astronomy",
                Recall.Both,
                Embed("astronomy", id)));
        }

        // Semantic only: astronomy prose that avoids the query words entirely.
        for (var i = 0; i < 24; i++)
        {
            id++;
            documents.Add(new Document(
                id,
                $"Observation log {id}",
                Sentence(id, AstronomyOther),
                "astronomy",
                Recall.SemanticOnly,
                Embed("astronomy", id)));
        }

        // Lexical only: the query words show up in a completely unrelated context.
        // "Nebula" and "Quasar" are plausible product names, which is exactly how lexical
        // false friends appear in real catalogues.
        for (var i = 0; i < 12; i++)
        {
            id++;
            var word = QueryWords[i % QueryWords.Length];
            documents.Add(new Document(
                id,
                $"{Capitalize(word)} dessert menu {id}",
                $"The {word} plate pairs a {Pick(CookingWords, id)} base with a {Pick(CookingWords, id + 1)} finish. "
                + $"Serve the {word} portion warm alongside a {Pick(CookingWords, id + 2)}.",
                "cooking",
                Recall.LexicalOnly,
                Embed("cooking", id)));
        }

        // Neither: filler from the remaining topics.
        foreach (var (topic, words) in new[]
                 {
                     ("databases", DatabaseWords),
                     ("aviation", AviationWords),
                     ("cooking", CookingWords)
                 })
        {
            for (var i = 0; i < 24; i++)
            {
                id++;
                documents.Add(new Document(
                    id,
                    $"{Capitalize(topic)} note {id}",
                    Sentence(id, words),
                    topic,
                    Recall.Neither,
                    Embed(topic, id)));
            }
        }

        return documents;
    }

    public static float[] QueryVector()
        => Centroids[Array.IndexOf(Topics, QueryTopic)];

    private static string[] Mix(string[] first, string[] second)
        => [.. first, .. second];

    private static string Sentence(int seed, string[] words)
    {
        var random = new Random(seed * 7919);
        return string.Join(' ', Enumerable.Range(0, 14).Select(_ => words[random.Next(words.Length)]));
    }

    private static string Pick(string[] words, int seed)
        => words[new Random(seed * 104729).Next(words.Length)];

    private static string Capitalize(string value)
        => char.ToUpperInvariant(value[0]) + value[1..];

    private static float[][] BuildCentroids()
    {
        var centroids = new float[Topics.Length][];
        for (var t = 0; t < Topics.Length; t++)
        {
            var random = new Random(1000 + t);
            centroids[t] = Normalize([.. Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1))]);
        }

        return centroids;
    }

    private static float[] Embed(string topic, int seed)
    {
        var centroid = Centroids[Array.IndexOf(Topics, topic)];
        var random = new Random(seed);
        var values = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            values[i] = centroid[i] + (float)(random.NextDouble() * 0.3 - 0.15);
        }

        return Normalize(values);
    }

    private static float[] Normalize(float[] values)
    {
        var norm = MathF.Sqrt(values.Sum(v => v * v));
        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }

        return values;
    }
}
