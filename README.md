# Vector and hybrid search with SQL Server in EF Core 11

Companion code for the article *Vector and Hybrid Search with SQL Server in EF Core 11*. Both
projects target .NET 11 and EF Core `11.0.0-rc.1.26425.128`.

| Project | What it is |
|---|---|
| [`HelpCenterSearch`](HelpCenterSearch) | An ASP.NET Core MVC app that runs one query through semantic, keyword and hybrid retrieval and shows the three result lists side by side. Clicking a result expands it. |
| [`VectorLab`](VectorLab) | A console probe that exercises each vector and full-text behaviour against a real server and prints what came back. The numbers the article measures come from a run of this. |

## Before you run anything

Neither project stores a connection string. `HelpCenterSearch/appsettings.json` ships with a
placeholder and expects user secrets or the `ConnectionStrings__Default` environment variable;
`VectorLab` reads `MSSQL_SERVER` / `MSSQL_DATABASE` / `MSSQL_USER` / `MSSQL_PASSWORD`. Do not commit
a real password to either.

`VectorLab` **drops and rebuilds the `Articles` table** in the database it points at, and changes
database-scoped settings. Point it at a disposable database.

## What you need

- .NET 11 SDK
- SQL Server 2025 or Azure SQL Database. Full-text search must be installed: the stock
  `mcr.microsoft.com/mssql/server:2025-latest` image does not ship it, and Azure SQL Database does.
- Approximate vector search additionally needs a vector index, which at the time of writing means
  Azure SQL Database or SQL database in Microsoft Fabric.

Each project has its own README with the details.
