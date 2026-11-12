using FaqBot.Core;
using FaqBot.Infrastructure;

namespace FaqBot.Api.Endpoints;

public static class ChatEndpoints
{
    public static RouteGroupBuilder MapChat(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat");
        group.MapPost("", ChatAsync).WithSummary("Ask the multilingual help center");
        group.MapGet("/diagnose", DiagnoseAsync).AddEndpointFilter<AdminKeyFilter>().WithSummary("Inspect retrieval and cache diagnostics");
        return group;
    }
    private static async Task<IResult> ChatAsync(ChatRequest request, IChatHandler handler, CancellationToken cancellationToken) =>
        ApiValidation.Valid(request) ? Results.Ok(await handler.HandleAsync(request, cancellationToken)) :
        ApiValidation.Problem(400, "invalid_chat", "SiteCode and Message are required; Message must not exceed 2000 characters.");
    private static async Task<IResult> DiagnoseAsync(string q, string? site, string? locale, IFaqIndexProvider indexes,
        IFaqRepository repository, CachedChatOrchestrator cache, CancellationToken cancellationToken)
    {
        var request = new ChatRequest(site ?? "demo", q, locale);
        if (!ApiValidation.Valid(request)) return ApiValidation.Problem(400, "invalid_query", "A nonempty query of up to 2000 characters is required.");
        var setting = await repository.GetSiteAsync(request.SiteCode, cancellationToken);
        if (setting is null) return ApiValidation.Problem(404, "site_not_found", "Site was not found.");
        var result = (await indexes.GetAsync(cancellationToken)).Search(q, locale);
        return Results.Ok(new { result.Normalized, result.Language, result.Tokens, result.ExpandedTokens,
            bm25 = result.Hits, result.Confidence, result.Margin, decision = setting.Enabled ? result.Decision.ToString() : "Disabled",
            decisionPath = !setting.Enabled ? "disabled" : result.Decision != DecisionKind.Fallback ? result.Decision.ToString() :
                setting.LlmFallbackEnabled ? "Fallback -> LLM -> answer or handoff" : "Fallback -> handoff",
            cacheHit = await cache.IsCachedAsync(request, cancellationToken) });
    }
}
public static class FaqEndpoints
{
    public static RouteGroupBuilder MapFaqs(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/faqs").WithTags("Knowledge base").AddEndpointFilter<AdminKeyFilter>();
        group.MapGet("", ListAsync).WithSummary("List FAQ translations by locale and category");
        group.MapGet("/{id}", GetAsync).WithSummary("Get a FAQ in all languages");
        group.MapPut("/{id}", PutAsync).WithSummary("Upsert a FAQ and invalidate the index");
        group.MapPost("/import", ImportAsync).WithSummary("Import an array of FAQ items atomically");
        group.MapDelete("/{id}", DeleteAsync).WithSummary("Disable a FAQ without deleting its content");
        return group;
    }
    private static async Task<IResult> ListAsync(string? locale, string? category, FaqRepository repository, CancellationToken cancellationToken)
    {
        if (locale is not null && !SupportedLocales.Contains(locale)) return ApiValidation.Problem(400, "invalid_locale", "Unsupported locale.");
        var items = (await repository.GetAllAsync(cancellationToken)).Where(f => category is null || f.Category == category);
        return Results.Ok(items.Select(f => locale is null ? f : f with { Translations = new() { [locale] = f.Translations[locale] } }));
    }
    private static async Task<IResult> GetAsync(string id, FaqRepository repository, CancellationToken cancellationToken)
    {
        var item = (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(f => f.Id == id);
        return item is null ? ApiValidation.Problem(404, "faq_not_found", "FAQ was not found.") : Results.Ok(item);
    }
    private static async Task<IResult> PutAsync(string id, FaqItem item, FaqRepository repository, CancellationToken cancellationToken)
    {
        if (!ApiValidation.Valid(item) || item.Id != id) return ApiValidation.Problem(400, "invalid_faq", "FAQ id must match the route and all four translations are required.");
        await repository.UpsertAsync([item], cancellationToken);
        return Results.Ok(item);
    }
    private static async Task<IResult> ImportAsync(FaqItem[] items, FaqRepository repository, CancellationToken cancellationToken)
    {
        if (items is null || items.Length is 0 or > 1000 || !items.All(ApiValidation.Valid) || items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            return ApiValidation.Problem(400, "invalid_import", "Provide 1 to 1000 valid FAQs with unique ids and all four translations.");
        await repository.UpsertAsync(items, cancellationToken);
        return Results.Ok(new { imported = items.Length });
    }
    private static async Task<IResult> DeleteAsync(string id, FaqRepository repository, CancellationToken cancellationToken)
    {
        var item = (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(f => f.Id == id);
        if (item is null) return ApiValidation.Problem(404, "faq_not_found", "FAQ was not found.");
        await repository.UpsertAsync([item with { Enabled = false }], cancellationToken);
        return Results.NoContent();
    }
}
public static class SiteEndpoints
{
    public static RouteGroupBuilder MapSites(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sites").WithTags("Sites").AddEndpointFilter<AdminKeyFilter>();
        group.MapGet("", async (FaqRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetSitesAsync(cancellationToken)))
            .WithSummary("List site settings");
        group.MapPut("/{code}", PutAsync).WithSummary("Update a site and invalidate cached responses");
        return group;
    }
    private static async Task<IResult> PutAsync(string code, SiteSetting site, FaqRepository repository, CancellationToken cancellationToken)
    {
        if (!ApiValidation.Valid(site) || code != site.SiteCode) return ApiValidation.Problem(400, "invalid_site", "A matching site code and supported locale are required.");
        await repository.SaveSiteAsync(site, cancellationToken);
        return Results.Ok(site);
    }
}
public static class AnalyticsEndpoints
{
    public static RouteGroupBuilder MapAnalytics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analytics").WithTags("Analytics").AddEndpointFilter<AdminKeyFilter>();
        group.MapGet("/unanswered", UnansweredAsync).WithSummary("Rank suggestions and handoffs by frequency");
        group.MapGet("/summary", SummaryAsync).WithSummary("Summarize sources, locales, and latency");
        return group;
    }
    private static async Task<IResult> UnansweredAsync(int? days, FaqRepository repository, TimeProvider time, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 365) return ApiValidation.Problem(400, "invalid_days", "Days must be between 1 and 365.");
        var logs = await repository.GetLogsAsync(time.GetUtcNow().AddDays(-(days ?? 7)), cancellationToken);
        return Results.Ok(logs.Where(l => l.Source is "handoff" or "suggestions").GroupBy(l => new { l.SiteCode, l.Locale, l.Message })
            .Select(g => new { g.Key.SiteCode, g.Key.Locale, g.Key.Message, count = g.Count() }).OrderByDescending(g => g.count));
    }
    private static async Task<IResult> SummaryAsync(int? days, FaqRepository repository, TimeProvider time, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 365) return ApiValidation.Problem(400, "invalid_days", "Days must be between 1 and 365.");
        var logs = await repository.GetLogsAsync(time.GetUtcNow().AddDays(-(days ?? 7)), cancellationToken);
        return Results.Ok(new { total = logs.Length, sources = logs.GroupBy(l => l.Source).Select(g => new { source = g.Key, count = g.Count(), ratio = (double)g.Count() / logs.Length }),
            locales = logs.GroupBy(l => l.Locale).Select(g => new { locale = g.Key, count = g.Count() }), averageLatencyMs = logs.Length == 0 ? 0 : logs.Average(l => l.LatencyMs) });
    }
}
