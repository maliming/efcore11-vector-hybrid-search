using HelpCenterSearch.Data;
using HelpCenterSearch.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<HelpCenterDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        // A serverless Azure SQL database that has auto-paused rejects the first connection with
        // error 40613 while it resumes, which would otherwise kill startup.
        sql => sql.EnableRetryOnFailure(maxRetryCount: 10, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null)));
builder.Services.AddSingleton<IEmbeddingGenerator, DeterministicEmbeddingGenerator>();
builder.Services.AddScoped<ArticleSearchService>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Search/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Search}/{action=Index}/{id?}")
    .WithStaticAssets();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HelpCenterDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(
        db,
        scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator>(),
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>());
}

app.Run();
