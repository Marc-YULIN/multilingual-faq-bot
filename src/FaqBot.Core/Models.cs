using System.ComponentModel.DataAnnotations;

namespace FaqBot.Core;

public sealed record FaqTranslation(string Question, string Answer, string[] AlternateQuestions, string[] Keywords);
public sealed record FaqItem(string Id, string Category, bool Enabled, Dictionary<string, FaqTranslation> Translations);
public static class SupportedLocales
{
    public static readonly string[] All = ["zh-TW", "en", "ko", "vi"];
    public static bool Contains(string? locale) => locale is not null && All.Contains(locale, StringComparer.Ordinal);
}
public sealed class RetrievalOptions
{
    [Range(0, 1)] public double AnswerThreshold { get; set; } = .62;
    [Range(0, 1)] public double SuggestThreshold { get; set; } = .35;
    [Range(0, 1)] public double MinMargin { get; set; } = .08;
    [Range(.01, 100)] public double K { get; set; } = 4;
}
public sealed record LanguageResult(string Locale, double Confidence);
public sealed record QueryToken(string Text, double Weight = 1);
public sealed record SearchHit(string FaqId, double Score, string[] MatchedTokens);
public sealed record ConfidenceResult(double Confidence, double Margin);
/// <summary>Determines whether retrieval can answer, suggest candidates, or defer.</summary>
public enum DecisionKind { Answer, Suggest, Fallback }
public sealed record RetrievalResult(string Normalized, LanguageResult Language, string[] Tokens,
    QueryToken[] ExpandedTokens, SearchHit[] Hits, double Confidence, double Margin, DecisionKind Decision);
public sealed record SiteSetting(string SiteCode, bool Enabled, bool LlmFallbackEnabled, string DefaultLocale,
    Dictionary<string, string> HandoffMessages);
public sealed record ChatRequest(string SiteCode, string Message, string? Locale = null, string? SessionId = null);
public sealed record Suggestion(string FaqId, string Question);
public sealed record ChatResponse(string Reply, string Locale, string Source, string? FaqId, double Confidence,
    Suggestion[] Suggestions, long LatencyMs);
public sealed record LlmContext(string FaqId, string Question, string Answer);
public sealed record LlmRequest(string Message, string Locale, LlmContext[] Context, double TopConfidence, double SuggestThreshold);
public sealed record LlmResponse(string Text, bool IsHandoff);
public sealed record ChatLogEntry(ChatRequest Request, ChatResponse Response, DateTimeOffset CreatedAt);
public interface ILlmClient { Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken); }
public interface IFaqRepository
{
    Task<FaqItem[]> GetAllAsync(CancellationToken cancellationToken);
    Task<SiteSetting?> GetSiteAsync(string code, CancellationToken cancellationToken);
    Task<long> GetVersionAsync(CancellationToken cancellationToken);
}
public interface IChatLogWriter { Task WriteAsync(ChatLogEntry entry, CancellationToken cancellationToken); }
public interface IFaqIndexProvider { Task<FaqEngine> GetAsync(CancellationToken cancellationToken); }
public interface IChatHandler { Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken cancellationToken); }
