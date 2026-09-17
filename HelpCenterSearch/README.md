# Help Center Search — EF Core 11 vector and hybrid search

A small ASP.NET Core MVC application that runs the same query through three retrieval strategies
and shows the results side by side:

| Column | API | Notes |
|---|---|---|
| Semantic | `EF.Functions.VectorDistance()` | Exact k-nearest-neighbor search over a `vector(1536)` column |
| Keyword | `FreeTextTable()` | SQL Server full-text search, with its relevance rank |
| Hybrid | Reciprocal Rank Fusion in application code | Fuses the two lists on rank **positions** |

It is the companion sample for the article *Vector and Hybrid Search with SQL Server in EF Core 11*.

## Requirements

- .NET 11 SDK (built against `11.0.100-rc.1.26425.128`)
- A SQL Server 2025 or Azure SQL Database instance **with full-text search installed**

Full-text search is a hard requirement, not a degraded mode. The initial migration creates the
full-text catalog and index and runs at startup, so on a server without the component the app fails
to start rather than rendering two empty columns. The stock `mcr.microsoft.com/mssql/server:2025-latest`
container image does **not** ship it — `SERVERPROPERTY('IsFullTextInstalled')` returns 0. Azure SQL
Database has it.

## Configure the connection string

`appsettings.json` ships with a placeholder:

```json
"ConnectionStrings": {
  "Default": "Server=<your-server>.database.windows.net;Database=HelpCenterSearch;User Id=<user>;Password=<password>;Encrypt=True"
}
```

**Do not put a real password in that file.** Use user secrets or an environment variable instead:

```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:Default" "Server=...;Database=HelpCenterSearch;User Id=...;Password=...;Encrypt=True"
```

```bash
export ConnectionStrings__Default="Server=...;Database=HelpCenterSearch;User Id=...;Password=...;Encrypt=True"
```

## Run

```bash
dotnet run
```

This is a single-instance local sample: seeding checks for an empty table and then inserts fixed
ids, so two instances starting against the same empty database at the same time would collide.

On startup the app applies migrations and seeds 32 help center articles. A new full-text index
populates asynchronously, so startup then waits for the indexed row count to catch up — up to 90
seconds, logged while it waits. Without that wait the first search comes back with an empty Keyword
column. The search page is the root URL; try `login keeps timing out`.

Clicking any result expands it below the three lists. The panel shows the article text, the
position it reached in each list, and the stored vector — read through a second, explicit
projection, because EF Core 11 leaves vector columns out of the SELECT that loads an entity.

## What to look for

The sample corpus is built so the two retrievers disagree. Searching `login keeps timing out`:

- **Semantic** returns authentication articles, including *Session expires too quickly* — which
  shares no words with the query.
- **Keyword** mixes in false friends: *Sign in sheet export template* is a reporting article that
  happens to contain "sign in", and *Payment retries after a declined card* is about billing.
- **Hybrid** pushes the authentication articles to the top and demotes the false friends.

Worth noticing: *Session expires too quickly* ranks first semantically but does not reach the top
of the hybrid list, because it never appears in the keyword list. With `rrfK = 60` and 20
candidates per list, anything both retrievers return outranks anything only one of them found.
That is a parameter choice, not a universal property of RRF — see the article for the arithmetic.

## Embeddings

`DeterministicEmbeddingGenerator` is a stand-in so the sample runs with no API key and produces the
same results everywhere. It clusters text into four topics; it is not a real embedding model.

For a real model, add a `Microsoft.Extensions.AI` provider package, inject
`IEmbeddingGenerator<string, Embedding<float>>`, and register an implementation of this project's
`IEmbeddingGenerator` that calls `GenerateVectorAsync`. Documents and queries must go through the
same model: distances are only meaningful within one model, and a different dimension count will
not fit the `vector(1536)` column.

Embeddings are derived data. The seed builds each one from the title and the body, so regenerate
them whenever either changes, and backfill every row if you switch models.

## The dotnet-ef tool

The `dotnet-ef` tool version must match the EF Core packages. With the 10.x tool, scaffolding fails
with `MissingMethodException: IIndex.get_Properties()`:

```bash
dotnet tool update -g dotnet-ef --version 11.0.0-rc.1.26425.128
```

## Approximate search

This sample uses exact search, which runs anywhere SQL Server 2025 runs. To use a DiskANN vector
index instead, add it to the model and swap the semantic query in `ArticleSearchService`:

```csharp
modelBuilder.Entity<Article>()
    .HasVectorIndex(a => a.Embedding)
    .HasMetric("cosine")
    .HasType("DiskANN");
```

```csharp
var semantic = await db.Articles
    .VectorSearch(a => a.Embedding, queryVector, "cosine")
    .OrderBy(r => r.Distance)
    .Take(CandidateCount)
    .WithApproximate()
    .Select(r => new { r.Value.Id, r.Value.Title, r.Value.Topic })
    .ToListAsync();
```

Two constraints apply, and the first one rules out the corpus shipped here. `CREATE VECTOR INDEX`
needs at least 100 non-null vectors, and this sample seeds 32 articles from application code *after*
every migration has run, so the index can never be created on this data. Taking the indexed path
means growing the corpus past 100 rows and writing the vectors in a data migration, with the
migration that creates the index ordered after it.

Second, on the SQL Server 2025 build tested for the article, EF's generated `VECTOR_SEARCH` syntax
is rejected — that path currently needs Azure SQL Database or SQL database in Microsoft Fabric, in a
region where vector search is available.
