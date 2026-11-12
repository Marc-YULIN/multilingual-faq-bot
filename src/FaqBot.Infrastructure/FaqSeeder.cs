using System.Text.Json;
using FaqBot.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FaqBot.Infrastructure;

public sealed class FaqSeeder(IDbContextFactory<FaqBotDbContext> factory, FaqRepository repository, IOptions<DataOptions> options)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.DataSource != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(connection.DataSource))!);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!await db.KnowledgeBaseVersions.AnyAsync(cancellationToken))
        {
            db.KnowledgeBaseVersions.Add(new());
            await db.SaveChangesAsync(cancellationToken);
        }
        if (!await db.FaqItems.AnyAsync(cancellationToken))
        {
            await using var stream = File.OpenRead(options.Value.SeedPath);
            var items = await JsonSerializer.DeserializeAsync<FaqItem[]>(stream, FaqJson.Options, cancellationToken) ?? [];
            await repository.UpsertAsync(items, cancellationToken);
        }
        foreach (var code in new[] { "demo", "docs" })
            if (!await db.Sites.AnyAsync(s => s.SiteCode == code, cancellationToken))
                await repository.SaveSiteAsync(new(code, true, code == "demo", "en", []), cancellationToken);
    }
}
