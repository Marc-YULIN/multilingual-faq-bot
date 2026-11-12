using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FaqBot.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FaqBot.Infrastructure;

public static class FaqJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
public sealed class FaqItemEntity
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; }
    public string TranslationsJson { get; set; } = "{}";
    public FaqItem ToItem() => new(Id, Category, Enabled, JsonSerializer.Deserialize<Dictionary<string, FaqTranslation>>(TranslationsJson, FaqJson.Options)!);
}
public sealed class SiteSettingEntity
{
    public string SiteCode { get; set; } = "";
    public bool Enabled { get; set; }
    public bool LlmFallbackEnabled { get; set; }
    public string DefaultLocale { get; set; } = "en";
    public string HandoffMessagesJson { get; set; } = "{}";
    public SiteSetting ToSetting() => new(SiteCode, Enabled, LlmFallbackEnabled, DefaultLocale,
        JsonSerializer.Deserialize<Dictionary<string, string>>(HandoffMessagesJson, FaqJson.Options)!);
}
public sealed class ChatLog
{
    public long Id { get; set; }
    public string SiteCode { get; set; } = "";
    public string? SessionId { get; set; }
    public string Locale { get; set; } = "";
    public string Message { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Source { get; set; } = "";
    public string? FaqId { get; set; }
    public double Confidence { get; set; }
    public long LatencyMs { get; set; }
    public DateTime CreatedAt { get; set; }
}
public sealed class KnowledgeBaseVersion { public int Id { get; set; } = 1; public long Version { get; set; } = 1; }
public sealed class FaqBotDbContext(DbContextOptions<FaqBotDbContext> options) : DbContext(options)
{
    public DbSet<FaqItemEntity> FaqItems => Set<FaqItemEntity>();
    public DbSet<SiteSettingEntity> Sites => Set<SiteSettingEntity>();
    public DbSet<ChatLog> ChatLogs => Set<ChatLog>();
    public DbSet<KnowledgeBaseVersion> KnowledgeBaseVersions => Set<KnowledgeBaseVersion>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FaqItemEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<SiteSettingEntity>().HasKey(x => x.SiteCode);
        modelBuilder.Entity<KnowledgeBaseVersion>().HasKey(x => x.Id);
        modelBuilder.Entity<ChatLog>().HasIndex(x => x.CreatedAt);
    }
}
public sealed class DataOptions
{
    [Required] public string SeedPath { get; set; } = "data/faq.seed.json";
    [Required] public string SynonymsPath { get; set; } = "data/synonyms.json";
}
public sealed class FaqRepository(IDbContextFactory<FaqBotDbContext> factory) : IFaqRepository, IChatLogWriter
{
    public async Task<FaqItem[]> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.FaqItems.AsNoTracking().OrderBy(f => f.Id).ToArrayAsync(cancellationToken)).Select(f => f.ToItem()).ToArray();
    }
    public async Task<SiteSetting?> GetSiteAsync(string code, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.Sites.AsNoTracking().SingleOrDefaultAsync(s => s.SiteCode == code, cancellationToken))?.ToSetting();
    }
    public async Task<SiteSetting[]> GetSitesAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.Sites.AsNoTracking().OrderBy(s => s.SiteCode).ToArrayAsync(cancellationToken)).Select(s => s.ToSetting()).ToArray();
    }
    public async Task<long> GetVersionAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.KnowledgeBaseVersions.Select(v => v.Version).SingleAsync(cancellationToken);
    }
    public async Task UpsertAsync(FaqItem[] items, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var item in items)
        {
            var entity = await db.FaqItems.FindAsync([item.Id], cancellationToken);
            if (entity is null) { entity = new() { Id = item.Id }; db.FaqItems.Add(entity); }
            entity.Category = item.Category; entity.Enabled = item.Enabled;
            entity.TranslationsJson = JsonSerializer.Serialize(item.Translations, FaqJson.Options);
        }
        await db.SaveChangesAsync(cancellationToken);
        await IncrementVersionAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task SaveSiteAsync(SiteSetting site, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await db.Sites.FindAsync([site.SiteCode], cancellationToken);
        if (entity is null) { entity = new() { SiteCode = site.SiteCode }; db.Sites.Add(entity); }
        entity.Enabled = site.Enabled; entity.LlmFallbackEnabled = site.LlmFallbackEnabled;
        entity.DefaultLocale = site.DefaultLocale; entity.HandoffMessagesJson = JsonSerializer.Serialize(site.HandoffMessages, FaqJson.Options);
        await db.SaveChangesAsync(cancellationToken);
        await IncrementVersionAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    private static Task<int> IncrementVersionAsync(FaqBotDbContext db, CancellationToken cancellationToken) =>
        db.KnowledgeBaseVersions.ExecuteUpdateAsync(s => s.SetProperty(v => v.Version, v => v.Version + 1), cancellationToken);
    public async Task WriteAsync(ChatLogEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.ChatLogs.Add(new() { SiteCode = entry.Request.SiteCode, SessionId = entry.Request.SessionId,
            Locale = entry.Response.Locale, Message = entry.Request.Message, Reply = entry.Response.Reply,
            Source = entry.Response.Source, FaqId = entry.Response.FaqId, Confidence = entry.Response.Confidence,
            LatencyMs = entry.Response.LatencyMs, CreatedAt = entry.CreatedAt.UtcDateTime });
        await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<ChatLog[]> GetLogsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatLogs.AsNoTracking().Where(l => l.CreatedAt >= since.UtcDateTime).ToArrayAsync(cancellationToken);
    }
}
