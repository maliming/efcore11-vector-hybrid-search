using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HelpCenterSearch.Migrations;

/// <inheritdoc />
public partial class _20260917072731_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Created before the table so a server without full-text search fails here, rather than
        // leaving a table behind that the next run cannot create again.
        migrationBuilder.Sql(
            "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ftCatalog') " +
            "CREATE FULLTEXT CATALOG [ftCatalog];",
            suppressTransaction: true);

        migrationBuilder.CreateTable(
            name: "Articles",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Topic = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Embedding = table.Column<SqlVector<float>>(type: "vector(1536)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Articles", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Articles_Title_Content",
            table: "Articles",
            columns: new[] { "Title", "Content" })
            .Annotation("SqlServer:FullTextCatalog", "ftCatalog")
            .Annotation("SqlServer:FullTextIndex", "PK_Articles");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Articles");

        // Only drop the catalog when nothing else is still indexed in it.
        migrationBuilder.Sql(
            "IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ftCatalog') " +
            "AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes fi " +
            "INNER JOIN sys.fulltext_catalogs c ON fi.fulltext_catalog_id = c.fulltext_catalog_id " +
            "WHERE c.name = 'ftCatalog') " +
            "DROP FULLTEXT CATALOG [ftCatalog];",
            suppressTransaction: true);
    }
}
