using HelpCenterSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace HelpCenterSearch.Data;

public class HelpCenterDbContext(DbContextOptions<HelpCenterDbContext> options) : DbContext(options)
{
    public DbSet<Article> Articles => Set<Article>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Article>(b =>
        {
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Title).IsRequired().HasMaxLength(256);
            b.Property(a => a.Topic).IsRequired().HasMaxLength(64);

            // HasFullTextIndex builds on HasIndex, so EF would otherwise cap this at nvarchar(450).
            b.Property(a => a.Content).IsRequired().HasColumnType("nvarchar(max)");

            b.HasFullTextIndex(a => new { a.Title, a.Content })
                .UseKeyIndex("PK_Articles")
                .UseCatalog("ftCatalog");
        });
    }
}
