using System.Collections.Concurrent;
using System.Text.Json;
using FaqBot.Core;
using Microsoft.Extensions.Options;

namespace FaqBot.Infrastructure;

public sealed class FaqIndexProvider(IFaqRepository repository, IOptions<DataOptions> data, RetrievalOptions options) : IFaqIndexProvider, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Snapshot(long Version, FaqEngine Engine);
    private Snapshot? snapshot;
    public async Task<FaqEngine> GetAsync(CancellationToken cancellationToken)
    {
        var version = await repository.GetVersionAsync(cancellationToken);
        var current = Volatile.Read(ref snapshot);
        if (current?.Version == version) return current.Engine;
        await gate.WaitAsync(cancellationToken);
        try
        {
            version = await repository.GetVersionAsync(cancellationToken);
            current = snapshot;
            if (current?.Version == version) return current.Engine;
            var items = await repository.GetAllAsync(cancellationToken);
            await using var stream = File.OpenRead(data.Value.SynonymsPath);
            var synonyms = await JsonSerializer.DeserializeAsync<Dictionary<string, string[][]>>(stream, FaqJson.Options, cancellationToken) ?? [];
            var engine = new FaqEngine(items, synonyms, options);
            Volatile.Write(ref snapshot, new(version, engine));
            return engine;
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}

public sealed class CachedChatOrchestrator(ChatOrchestrator inner, IFaqRepository repository, IChatLogWriter logs, TimeProvider time) : IChatHandler
{
    private sealed record CacheKey(string Site, string Locale, string Message, long Version);
    private sealed record Entry(ChatResponse Response, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<CacheKey, Entry> entries = new();
    private async Task<CacheKey> KeyAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var locale = LanguageDetector.Detect(request.Message, request.Locale).Locale;
        return new(request.SiteCode, locale, TextNormalizer.Normalize(request.Message, locale), await repository.GetVersionAsync(cancellationToken));
    }
    public async Task<bool> IsCachedAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(request, cancellationToken);
        return entries.TryGetValue(key, out var entry) && entry.Expires > time.GetUtcNow();
    }
    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var key = await KeyAsync(request, cancellationToken);
        if (entries.TryGetValue(key, out var cached) && cached.Expires > time.GetUtcNow())
        {
            var response = cached.Response with { LatencyMs = (long)time.GetElapsedTime(started).TotalMilliseconds };
            await logs.WriteAsync(new(request, response, time.GetUtcNow()), cancellationToken);
            return response;
        }
        entries.TryRemove(key, out _);
        var result = await inner.HandleAsync(request, cancellationToken);
        if (result.Source is "faq" or "suggestions" or "llm")
        {
            if (entries.Count >= 1000) entries.Clear();
            entries[key] = new(result, time.GetUtcNow().AddMinutes(result.Source == "llm" ? 2 : 10));
        }
        return result;
    }
}
