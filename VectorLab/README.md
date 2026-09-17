# VectorLab — the measurements behind the article

A console probe that runs each vector and full-text behaviour described in *Vector and Hybrid
Search with SQL Server in EF Core 11* against a real server and prints what came back. The numbers
the article measures itself — the generated SQL, the full-text `RANK` values, the retriever lists,
the fused ranking table and the errors from the documented single-query shape — come from a run of
this program. Figures the article quotes from Microsoft, such as the 9x/22x projection benchmark and
the 50,000-candidate guideline, are cited, not measured here.

## The corpus

120 documents built so the two retrievers disagree, which is what makes a fusion function's
behaviour observable:

| Group | Count | Matches |
|---|---|---|
| Both | 12 | the query words **and** the query topic |
| Semantic only | 24 | the topic, sharing no query words |
| Lexical false friends | 12 | the query words, in an unrelated topic — dessert items named *Nebula* and *Quasar* |
| Unrelated | 72 | neither |

The query is the text `nebula quasar` plus an astronomy topic vector.

Embeddings are deterministic topic centroids of **8 dimensions**, not 1,536. The lab measures
ranking behaviour, not embedding quality, and small vectors keep the printed output readable. The
count still clears the 100-row minimum that `CREATE VECTOR INDEX` requires.

## This rebuilds the database it points at

Every run drops the full-text index, the vector index and the whole `Articles` table, then recreates
and reseeds them. It also sets `COMPATIBILITY_LEVEL` and `PREVIEW_FEATURES` on the target database.
**Point it at a disposable database.** If the target already has an `Articles` table the probe stops
and says so; set `VECTORLAB_ALLOW_DROP=1` to let it continue.

## Run it

```bash
export MSSQL_SERVER="your-server.database.windows.net"
export MSSQL_DATABASE="VectorLab"
export MSSQL_USER="your-user"
export MSSQL_PASSWORD="your-password"

dotnet run
```

`MSSQL_PASSWORD` is required; the other three default to `localhost,1434`, `VectorLab` and `sa`.
Nothing is written to disk, so no connection string ends up in the repository.

Against a container the probe creates the database and sets `COMPATIBILITY_LEVEL = 170` and
`PREVIEW_FEATURES` itself. Against Azure SQL Database it uses the database it is pointed at.

## What it prints

The steps run in order and later ones depend on earlier ones, but each is failure-isolated: a step
that a given server cannot run is reported and the rest are still attempted.

- the DDL EF generates for a model with vector and full-text indexes
- server version, edition and `IsFullTextInstalled`
- exact kNN with `EF.Functions.VectorDistance()`
- `CREATE VECTOR INDEX`, then `VectorSearch()` with `WithApproximate()`
- full-text catalog and index creation, and the wait for asynchronous population
- ranking through `FreeTextTable()` and `ContainsTable()`
- the documented single-query hybrid shape, and three isolation steps that narrow down why it fails
- reciprocal rank fusion over rank positions, on both the exact and the approximate vector path

`sample-output.txt` in this folder is a recorded run against Azure SQL Database, kept so the numbers
quoted in the article stay tied to a server version.

The full-text steps need a server with full-text search installed. The approximate steps need a
vector index, which on the builds tested for the article means Azure SQL Database or SQL database
in Microsoft Fabric. The probe connects with a SQL login to a `.database.windows.net` host or a
container; it does not implement the Microsoft Entra authentication a Fabric SQL database expects.
