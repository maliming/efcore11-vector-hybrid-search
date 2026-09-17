using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace VectorLab;

/// <summary>
///     A context that configures the vector and full-text indexes through the EF Core model rather
///     than raw SQL, so the DDL EF actually generates can be inspected.
/// </summary>
public class ModelledContext(string connectionString) : DbContext
{
    public DbSet<ModelledArticle> Articles => Set<ModelledArticle>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlServer(connectionString, o => o.UseCompatibilityLevel(170));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasFullTextCatalog("ftCatalog");

        modelBuilder.Entity<ModelledArticle>(b =>
        {
            b.ToTable("ModelledArticles");
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Title).HasMaxLength(200);
            b.Property(a => a.Content).HasColumnType("nvarchar(max)");
            b.Property(a => a.Embedding).HasColumnType($"vector({Corpus.Dimensions})");

            b.HasVectorIndex(a => a.Embedding)
                .HasMetric("cosine")
                .HasType("DiskANN");

            b.HasFullTextIndex(a => new { a.Title, a.Content })
                .UseKeyIndex("PK_ModelledArticles")
                .UseCatalog("ftCatalog");
        });
    }
}

public class ModelledArticle
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public SqlVector<float> Embedding { get; set; }
}
