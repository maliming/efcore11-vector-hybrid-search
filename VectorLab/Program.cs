using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using VectorLab;

var password = Environment.GetEnvironmentVariable("MSSQL_PASSWORD")
    ?? Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")
    ?? throw new InvalidOperationException(
        "Set MSSQL_PASSWORD, plus MSSQL_SERVER / MSSQL_USER / MSSQL_DATABASE when they differ from the defaults.");
var server = Environment.GetEnvironmentVariable("MSSQL_SERVER") ?? "localhost,1434";
var user = Environment.GetEnvironmentVariable("MSSQL_USER") ?? "sa";
var database = Environment.GetEnvironmentVariable("MSSQL_DATABASE") ?? "VectorLab";

// The name goes into DDL, where it cannot be a parameter, so only accept one that needs no escaping.
if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^[A-Za-z][A-Za-z0-9_]{0,62}$"))
{
    throw new InvalidOperationException($"MSSQL_DATABASE must match ^[A-Za-z][A-Za-z0-9_]{{0,62}}$, but was '{database}'.");
}

var isAzure = server.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase);

SqlConnectionStringBuilder Connection(string initialCatalog) => new()
{
    DataSource = server,
    InitialCatalog = initialCatalog,
    UserID = user,
    Password = password,
    Encrypt = isAzure,
    TrustServerCertificate = !isAzure,
    // A serverless database that has auto-paused needs well over the 15 second default to resume.
    ConnectTimeout = 120
};

var masterConnectionString = Connection("master").ConnectionString;
var connectionString = Connection(database).ConnectionString;

Console.WriteLine($"target: {server}/{database} ({(isAzure ? "Azure SQL Database" : "SQL Server container")})");

// Every run rebuilds Articles from scratch, so refuse a database that already holds one unless the
// caller says it is disposable.
if (Environment.GetEnvironmentVariable("VECTORLAB_ALLOW_DROP") != "1")
{
    await using var check = new ProbeContext(masterConnectionString);
    var existing = await check.Database
        .SqlQuery<int>($"SELECT CASE WHEN DB_ID({database}) IS NULL THEN 0 ELSE 1 END AS [Value]")
        .SingleAsync();

    if (existing == 1)
    {
        await using var target = new ProbeContext(connectionString);
        var userTables = await target.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE is_ms_shipped = 0")
            .SingleAsync();

        if (userTables > 0)
        {
            Console.WriteLine(
                $"'{database}' already holds {userTables} table(s). This probe drops and rebuilds Articles and " +
                "changes database-scoped settings. Point MSSQL_DATABASE at a disposable database, or set " +
                "VECTORLAB_ALLOW_DROP=1 to continue.");
            return;
        }
    }
}

await Step("Model-configured vector and full-text indexes: the DDL EF generates", async () =>
{
    await using var context = new ModelledContext(connectionString);
    var script = context.Database.GenerateCreateScript();
    foreach (var line in script.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0))
    {
        Console.WriteLine("  " + line);
    }

    await Task.CompletedTask;
});

await Step("Server info", async () =>
{
    await using var context = new ProbeContext(connectionString);
    var info = await context.Database
        .SqlQuery<string>(
            $"SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + N' | ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) + N' | engine=' + CAST(SERVERPROPERTY('EngineEdition') AS nvarchar(8)) + N' | fulltext=' + CAST(SERVERPROPERTY('IsFullTextInstalled') AS nvarchar(4)) AS [Value]")
        .SingleAsync();
    Console.WriteLine($"  {info}");
});

await Step("Prepare database", async () =>
{
    if (isAzure)
    {
        await using var azureContext = new ProbeContext(connectionString);
        var level = await azureContext.Database
            .SqlQuery<byte>($"SELECT CAST(compatibility_level AS tinyint) AS [Value] FROM sys.databases WHERE name = DB_NAME()")
            .SingleAsync();
        Console.WriteLine($"  provisioned by Azure, compatibility_level = {level}, PREVIEW_FEATURES not required");
        return;
    }

    await using var master = new ProbeContext(masterConnectionString);

    // A database name cannot be a parameter in DDL; the value comes from MSSQL_DATABASE.
#pragma warning disable EF1002
    await master.Database.ExecuteSqlRawAsync(
        $"""
         IF DB_ID(N'{database}') IS NULL CREATE DATABASE [{database}];
         ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 170;
         """);
#pragma warning restore EF1002

    await using var context = new ProbeContext(connectionString);
    await context.Database.ExecuteSqlRawAsync("ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;");
    Console.WriteLine("  database created, compatibility level 170, PREVIEW_FEATURES = ON");
});

await Step("Recreate Articles table", async () =>
{
    await using var context = new ProbeContext(connectionString);
    await context.Database.ExecuteSqlRawAsync(
        $"""
         IF EXISTS (SELECT 1 FROM sys.fulltext_indexes i JOIN sys.objects o ON i.object_id = o.object_id WHERE o.name = 'Articles')
             DROP FULLTEXT INDEX ON [Articles];
         IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Articles_Embedding')
             DROP INDEX [IX_Articles_Embedding] ON [Articles];
         DROP TABLE IF EXISTS [Articles];
         CREATE TABLE [Articles] (
             [Id] int NOT NULL,
             [Title] nvarchar(200) NOT NULL,
             [Content] nvarchar(max) NOT NULL,
             [Topic] nvarchar(50) NOT NULL,
             [Recall] nvarchar(20) NOT NULL,
             [Embedding] vector({Corpus.Dimensions}) NOT NULL,
             CONSTRAINT [PK_Articles] PRIMARY KEY ([Id])
         );
         """);
    Console.WriteLine("  table created");
});

await Step("Seed the adversarial corpus", async () =>
{
    await using var context = new ProbeContext(connectionString);
    var documents = Corpus.Build();
    context.Articles.AddRange(documents.Select(d => new Article
    {
        Id = d.Id,
        Title = d.Title,
        Content = d.Content,
        Topic = d.Topic,
        Recall = d.Recall.ToString(),
        Embedding = new SqlVector<float>(d.Embedding)
    }));
    var written = await context.SaveChangesAsync();
    var byRecall = documents.GroupBy(d => d.Recall).OrderBy(g => g.Key.ToString());
    Console.WriteLine($"  {written} rows: " + string.Join(", ", byRecall.Select(g => $"{g.Key}={g.Count()}")));
});

await Step("Vector properties are not projected by default (EF 11 change)", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    var articles = await context.Articles.OrderBy(a => a.Id).Take(2).ToListAsync();
    Console.WriteLine($"  loaded {articles.Count}, first embedding length = {articles[0].Embedding.Length}");
});

await Step("Exact kNN with EF.Functions.VectorDistance", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    var query = new SqlVector<float>(Corpus.QueryVector());
    var hits = await context.Articles
        .OrderBy(a => EF.Functions.VectorDistance("cosine", a.Embedding, query))
        .Select(a => new { a.Id, a.Topic, a.Recall, a.Title })
        .Take(5)
        .ToListAsync();
    foreach (var hit in hits)
    {
        Console.WriteLine($"  {hit.Recall,-12} [{hit.Topic,-9}] {hit.Title}");
    }
});

await Step("CREATE VECTOR INDEX", async () =>
{
    await using var context = new ProbeContext(connectionString);
    await context.Database.ExecuteSqlRawAsync(
        "CREATE VECTOR INDEX [IX_Articles_Embedding] ON [Articles]([Embedding]) WITH (METRIC = 'cosine', TYPE = 'DiskANN');");
    var indexes = await context.Database
        .SqlQuery<string>(
            $"SELECT CAST(name AS nvarchar(128)) COLLATE DATABASE_DEFAULT + N' type=' + CAST(vector_index_type AS nvarchar(32)) COLLATE DATABASE_DEFAULT + N' metric=' + CAST(distance_metric AS nvarchar(32)) COLLATE DATABASE_DEFAULT AS [Value] FROM sys.vector_indexes")
        .ToListAsync();
    Console.WriteLine("  " + string.Join("\n  ", indexes));
});

await Step("ANN search: VectorSearch() + WithApproximate()", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    var query = new SqlVector<float>(Corpus.QueryVector());
    var hits = await context.Articles
        .VectorSearch(a => a.Embedding, query, "cosine")
        .OrderBy(r => r.Distance)
        .Take(5)
        .WithApproximate()
        .ToListAsync();
    foreach (var hit in hits)
    {
        Console.WriteLine($"  {hit.Value.Recall,-12} [{hit.Value.Topic,-9}] {hit.Value.Title} distance={hit.Distance:F6}");
    }
});

await Step("Create full-text catalog and index", async () =>
{
    await using var context = new ProbeContext(connectionString);
    await context.Database.ExecuteSqlRawAsync(
        """
        IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ftCatalog')
            CREATE FULLTEXT CATALOG [ftCatalog];
        """);
    await context.Database.ExecuteSqlRawAsync(
        """
        CREATE FULLTEXT INDEX ON [Articles]([Title], [Content])
        KEY INDEX [PK_Articles] ON [ftCatalog] WITH CHANGE_TRACKING = AUTO;
        """);
    Console.WriteLine("  catalog and index created");
});

await Step("Wait for full-text population", async () =>
{
    await using var context = new ProbeContext(connectionString);
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        // Table-level counts, not the catalog PopulateStatus that is marked for removal.
        var indexed = await context.Database
            .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('Articles'), 'TableFulltextItemCount') AS int) AS [Value]")
            .SingleAsync();
        var failed = await context.Database
            .SqlQuery<int>($"SELECT CAST(OBJECTPROPERTYEX(OBJECT_ID('Articles'), 'TableFulltextFailCount') AS int) AS [Value]")
            .SingleAsync();

        if (failed > 0)
        {
            throw new InvalidOperationException($"full-text population reported {failed} failed rows");
        }

        if (indexed >= Corpus.Build().Count)
        {
            Console.WriteLine($"  populated after {attempt} checks, {indexed} rows indexed");
            return;
        }

        await Task.Delay(2000);
    }

    throw new TimeoutException("full-text population did not complete in time");
});

await Step("Full-text ranking with FreeTextTable()", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    var hits = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: 5)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { a.Id, a.Topic, a.Recall, a.Title, fts.Rank })
        .OrderByDescending(x => x.Rank)
        .ThenBy(x => x.Id)
        .ToListAsync();
    foreach (var hit in hits)
    {
        Console.WriteLine($"  {hit.Recall,-12} [{hit.Topic,-9}] {hit.Title} rank={hit.Rank}");
    }
});

await Step("Full-text ranking with ContainsTable()", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    var hits = await context.Articles
        .ContainsTable<Article, int>("nebula OR quasar", topN: 5)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { a.Id, a.Topic, a.Recall, a.Title, fts.Rank })
        .OrderByDescending(x => x.Rank)
        .ThenBy(x => x.Id)
        .ToListAsync();
    foreach (var hit in hits)
    {
        Console.WriteLine($"  {hit.Recall,-12} [{hit.Topic,-9}] {hit.Title} rank={hit.Rank}");
    }
});

await Step("Isolate the failure - scalars only, no entity across the FULL JOIN", async () =>
{
    await using var context = new ProbeContext(connectionString);
    const int k = 20;

    var results = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { a.Id, a.Title, a.Recall, fts.Rank })
        .FullJoin(
            context.Articles
                .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
                .OrderBy(r => r.Distance)
                .Take(k)
                .WithApproximate()
                .Select(r => new { r.Value.Id, r.Value.Title, r.Value.Recall, r.Distance }),
            fts => fts.Id,
            vs => vs.Id,
            (fts, vs) => new
            {
                FullTextRank = fts == null ? null : (int?)fts.Rank,
                VectorDistance = vs == null ? null : (double?)vs.Distance
            })
        .Take(10)
        .ToListAsync();

    var lexicalOnly = results.Count(r => r.VectorDistance is null);
    var semanticOnly = results.Count(r => r.FullTextRank is null);
    Console.WriteLine($"  materialized {results.Count} rows; lexical-only={lexicalOnly}, semantic-only={semanticOnly}");
});

await Step("Isolate the failure - conditional entity across the FULL JOIN", async () =>
{
    await using var context = new ProbeContext(connectionString);
    const int k = 20;

    var results = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { Article = a, fts.Rank })
        .FullJoin(
            context.Articles
                .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
                .OrderBy(r => r.Distance)
                .Take(k)
                .WithApproximate()
                .Select(r => new { Article = r.Value, r.Distance }),
            fts => fts.Article.Id,
            vs => vs.Article.Id,
            (fts, vs) => new { Article = fts != null ? fts.Article : vs!.Article })
        .Select(x => x.Article.Title)
        .Take(10)
        .ToListAsync();

    Console.WriteLine($"  materialized {results.Count} titles");
});

await Step("Isolate the failure - entity plus nullable scalars, no score expression", async () =>
{
    await using var context = new ProbeContext(connectionString);
    const int k = 20;

    var results = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { Article = a, fts.Rank })
        .FullJoin(
            context.Articles
                .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
                .OrderBy(r => r.Distance)
                .Take(k)
                .WithApproximate()
                .Select(r => new { Article = r.Value, r.Distance }),
            fts => fts.Article.Id,
            vs => vs.Article.Id,
            (fts, vs) => new
            {
                Article = fts != null ? fts.Article : vs!.Article,
                FullTextRank = fts == null ? null : (int?)fts.Rank,
                VectorDistance = vs == null ? null : (double?)vs.Distance
            })
        .Select(x => new { x.Article.Title, x.FullTextRank, x.VectorDistance })
        .Take(10)
        .ToListAsync();

    Console.WriteLine($"  materialized {results.Count} rows");
});

await Step("Hybrid search A - RRF exactly as the docs write it", async () =>
{
    await using var context = new ProbeContext(connectionString, logSql: true);
    const int k = 20;

    var results = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { Article = a, fts.Rank })
        .FullJoin(
            context.Articles
                .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
                .OrderBy(r => r.Distance)
                .Take(k)
                .WithApproximate()
                // VectorSearchResult<T> is a readonly struct and cannot be compared to null in the
                // result selector, so project it to a reference type first.
                .Select(r => new { Article = r.Value, r.Distance }),
            fts => fts.Article.Id,
            vs => vs.Article.Id,
            (fts, vs) => new
            {
                Article = fts != null ? fts.Article : vs!.Article,
                FullTextRank = fts == null ? null : (int?)fts.Rank,
                VectorDistance = vs == null ? null : (double?)vs.Distance
            })
        .Select(x => new
        {
            x.Article.Title,
            x.Article.Recall,
            x.FullTextRank,
            x.VectorDistance,
            Score = (x.FullTextRank == null ? 0.0 : 1.0 / (k + x.FullTextRank.Value))
                + (x.VectorDistance == null ? 0.0 : 1.0 / (k + x.VectorDistance.Value))
        })
        .OrderByDescending(x => x.Score)
        .Take(10)
        .ToListAsync();

    Console.WriteLine(Header("ftRank", "distance"));
    foreach (var row in results)
    {
        var ft = row.FullTextRank?.ToString() ?? "-";
        var vec = row.VectorDistance is null ? "-" : row.VectorDistance.Value.ToString("F4");
        Console.WriteLine(Row(row.Recall, row.Title, ft, vec, row.Score));
    }
});

await Step("Workaround - same score without calling .Value", async () =>
{
    await using var context = new ProbeContext(connectionString);
    const int k = 20;

    var results = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { Article = a, fts.Rank })
        .FullJoin(
            context.Articles
                .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
                .OrderBy(r => r.Distance)
                .Take(k)
                .WithApproximate()
                .Select(r => new { Article = r.Value, r.Distance }),
            fts => fts.Article.Id,
            vs => vs.Article.Id,
            (fts, vs) => new
            {
                Article = fts != null ? fts.Article : vs!.Article,
                FullTextRank = fts == null ? null : (int?)fts.Rank,
                VectorDistance = vs == null ? null : (double?)vs.Distance
            })
        .Select(x => new
        {
            x.Article.Title,
            x.Article.Recall,
            x.FullTextRank,
            x.VectorDistance,
            // No .Value anywhere: null-coalesce first, then divide.
            Score = (x.FullTextRank == null ? 0.0 : 1.0 / (k + (x.FullTextRank ?? 0)))
                + (x.VectorDistance == null ? 0.0 : 1.0 / (k + (x.VectorDistance ?? 0.0)))
        })
        .OrderByDescending(x => x.Score)
        .Take(10)
        .ToListAsync();

    Console.WriteLine(Header("ftRank", "distance"));
    foreach (var row in results)
    {
        Console.WriteLine(Row(
            row.Recall,
            row.Title,
            row.FullTextRank?.ToString() ?? "-",
            row.VectorDistance is null ? "-" : row.VectorDistance.Value.ToString("F4"),
            row.Score));
    }
});

await Step("Hybrid search B - RRF over rank positions", async () =>
{
    await using var context = new ProbeContext(connectionString);
    const int k = 20;
    const int rrfK = 60; // the constant from the original RRF paper

    // Retrieve each list separately so both sides can be reduced to 1-based positions.
    var lexical = await context.Articles
        .FreeTextTable<Article, int>(Corpus.QueryText, topN: k)
        .Join(context.Articles, fts => fts.Key, a => a.Id, (fts, a) => new { a.Id, a.Title, a.Recall, fts.Rank })
        .OrderByDescending(x => x.Rank)
        .ThenBy(x => x.Id)
        .ToListAsync();

    var semantic = await context.Articles
        .VectorSearch(a => a.Embedding, new SqlVector<float>(Corpus.QueryVector()), "cosine")
        .OrderBy(r => r.Distance)
        .Take(k)
        .WithApproximate()
        .Select(r => new { r.Value.Id, r.Value.Title, r.Value.Recall, r.Distance })
        .ToListAsync();

    var lexicalPosition = lexical.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);
    var semanticPosition = semantic.Select((x, i) => (x.Id, Position: i + 1)).ToDictionary(x => x.Id, x => x.Position);

    var meta = lexical.Select(x => (x.Id, x.Title, x.Recall))
        .Concat(semantic.Select(x => (x.Id, x.Title, x.Recall)))
        .DistinctBy(x => x.Id)
        .ToDictionary(x => x.Id);

    var fused = meta.Keys
        .Select(id => new
        {
            Id = id,
            meta[id].Title,
            meta[id].Recall,
            LexicalPosition = lexicalPosition.TryGetValue(id, out var lp) ? lp : (int?)null,
            SemanticPosition = semanticPosition.TryGetValue(id, out var sp) ? sp : (int?)null,
            Score = (lexicalPosition.TryGetValue(id, out var l) ? 1.0 / (rrfK + l) : 0.0)
                + (semanticPosition.TryGetValue(id, out var v) ? 1.0 / (rrfK + v) : 0.0)
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Id)
        .Take(10)
        .ToList();

    Console.WriteLine(Header("lex#", "sem#"));
    foreach (var hit in fused)
    {
        Console.WriteLine(Row(
            hit.Recall,
            hit.Title,
            hit.LexicalPosition?.ToString() ?? "-",
            hit.SemanticPosition?.ToString() ?? "-",
            hit.Score));
    }
});

static string Header(string left, string right)
    => $"  {"recall",-12} {"title",-28} {left,9} {right,9} {"score",9}";

static string Row(string recall, string title, string left, string right, double score)
    => $"  {recall,-12} {Truncate(title, 28),-28} {left,9} {right,9} {score,9:F5}";

static string Truncate(string value, int length)
    => value.Length <= length ? value : value[..(length - 3)] + "...";

await Step("Article snippet - approximate path", async () =>
{
    await using var context = new ProbeContext(connectionString);
    var rows = (dynamic)await ArticleSnippet.HybridSearchAsync(
        context, Corpus.QueryText, new SqlVector<float>(Corpus.QueryVector()));

    Console.WriteLine($"  {"title",-24} {"lexical#",9} {"semantic#",10} {"score",9}");
    foreach (var row in rows)
    {
        string lex = row.LexicalPosition?.ToString() ?? "-";
        string sem = row.SemanticPosition?.ToString() ?? "-";
        Console.WriteLine($"  {(string)row.Title,-24} {lex,9} {sem,10} {(double)row.Score,9:F5}");
    }
});

await Step("Article snippet - exact kNN fusion (recommended path)", async () =>
{
    await using var context = new ProbeContext(connectionString);
    var rows = await ArticleSnippetExact.HybridSearchAsync(
        context, Corpus.QueryText, new SqlVector<float>(Corpus.QueryVector()));

    Console.WriteLine($"  {"title",-24} {"lexical#",9} {"semantic#",10} {"score",9}");
    foreach (var r in rows)
    {
        Console.WriteLine($"  {r.Title,-24} {r.LexicalPosition?.ToString() ?? "-",9} {r.SemanticPosition?.ToString() ?? "-",10} {r.Score,9:F5}");
    }
});

static async Task Step(string name, Func<Task> action)
{
    Console.WriteLine();
    Console.WriteLine($"=== {name} ===");
    try
    {
        await action();
        Console.WriteLine("RESULT: OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"RESULT: FAILED — {ex.GetType().Name}");
        for (var e = ex; e is not null; e = e.InnerException)
        {
            Console.WriteLine($"  {e.GetType().Name}: {e.Message}");
        }

        var frames = (ex.StackTrace ?? string.Empty)
            .Split('\n')
            .Where(f => !f.Contains("System.Runtime.CompilerServices"))
            .Take(8);
        foreach (var frame in frames)
        {
            Console.WriteLine($"    {frame.Trim()}");
        }
    }
}

public class Article
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string Topic { get; set; } = null!;
    public string Recall { get; set; } = null!;
    public SqlVector<float> Embedding { get; set; }
}

public class ProbeContext(string connectionString, bool logSql = false) : DbContext
{
    public DbSet<Article> Articles => Set<Article>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlServer(connectionString, o => o.UseCompatibilityLevel(170));
        if (logSql)
        {
            options.LogTo(
                Console.WriteLine,
                [
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted,
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandError
                ]);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Article>(b =>
        {
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Title).HasMaxLength(200);
            b.Property(a => a.Topic).HasMaxLength(50);
            b.Property(a => a.Recall).HasMaxLength(20);
            b.Property(a => a.Embedding).HasColumnType($"vector({Corpus.Dimensions})");
        });
    }
}
