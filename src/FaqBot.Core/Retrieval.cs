namespace FaqBot.Core;

public sealed class Bm25Index
{
    private readonly Dictionary<string, Dictionary<string, int>> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> documentFrequency = new(StringComparer.Ordinal);
    private readonly double averageLength;
    public Bm25Index(IEnumerable<FaqItem> items, string locale)
    {
        foreach (var item in items.Where(f => f.Enabled && f.Translations.ContainsKey(locale)))
        {
            var translation = item.Translations[locale];
            var text = string.Join(" ", new[] { translation.Question }.Concat(translation.AlternateQuestions)
                .Concat(translation.Keywords).Concat(translation.Keywords));
            var counts = Tokenizer.Tokenize(TextNormalizer.Normalize(text, locale), locale)
                .GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            documents[item.Id] = counts;
            foreach (var token in counts.Keys) documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
        }
        averageLength = documents.Count == 0 ? 1 : Math.Max(1, documents.Values.Average(d => d.Values.Sum()));
    }
    public SearchHit[] Search(IEnumerable<QueryToken> tokens, int top = 10)
    {
        var query = tokens.GroupBy(t => t.Text, StringComparer.Ordinal).Select(g => g.MaxBy(t => t.Weight)!).ToArray();
        return documents.Select(document =>
        {
            double score = 0;
            var matched = new List<string>();
            var length = document.Value.Values.Sum();
            foreach (var token in query)
            {
                if (!document.Value.TryGetValue(token.Text, out var frequency)) continue;
                var df = documentFrequency[token.Text];
                var idf = Math.Log(1 + (documents.Count - df + .5) / (df + .5));
                score += token.Weight * idf * frequency * 2.2 / (frequency + 1.2 * (.25 + .75 * length / averageLength));
                matched.Add(token.Text);
            }
            return new SearchHit(document.Key, score, matched.ToArray());
        }).Where(h => h.Score > 0).OrderByDescending(h => h.Score).ThenBy(h => h.FaqId, StringComparer.Ordinal).Take(top).ToArray();
    }
}
public static class ConfidenceScorer
{
    public static ConfidenceResult Score(SearchHit[] hits, double k = 4, double coverage = 1)
    {
        var first = hits.FirstOrDefault()?.Score ?? 0;
        if (first <= 0) return new(0, 0);
        var second = hits.Skip(1).FirstOrDefault()?.Score ?? 0;
        var margin = Math.Clamp((first - second) / first, 0, 1);
        return new(Math.Clamp(first / (first + k) * (.75 + .25 * margin) * Math.Clamp(coverage, 0, 1), 0, 1), margin);
    }
}
public static class AnswerDecision
{
    public static DecisionKind Decide(double confidence, double margin, RetrievalOptions options) =>
        confidence >= options.AnswerThreshold && margin >= options.MinMargin ? DecisionKind.Answer :
        confidence >= options.SuggestThreshold ? DecisionKind.Suggest : DecisionKind.Fallback;
}
public sealed class FaqEngine
{
    private readonly Dictionary<string, Bm25Index> indexes;
    private readonly SynonymExpander expander;
    private readonly RetrievalOptions options;
    public IReadOnlyDictionary<string, FaqItem> Items { get; }
    public FaqEngine(IEnumerable<FaqItem> items, Dictionary<string, string[][]> synonyms, RetrievalOptions options)
    {
        Items = items.Where(f => f.Enabled).ToDictionary(f => f.Id, StringComparer.Ordinal);
        indexes = SupportedLocales.All.ToDictionary(locale => locale, locale => new Bm25Index(Items.Values, locale), StringComparer.Ordinal);
        expander = new(synonyms);
        this.options = options;
    }
    public RetrievalResult Search(string message, string? locale = null)
    {
        var language = LanguageDetector.Detect(message, locale);
        var normalized = TextNormalizer.Normalize(message, language.Locale);
        var tokens = Tokenizer.Tokenize(normalized, language.Locale);
        var expanded = expander.Expand(normalized, language.Locale, tokens);
        var hits = indexes[language.Locale].Search(expanded);
        var queryTerms = tokens.Distinct(StringComparer.Ordinal).ToArray();
        var matched = hits.FirstOrDefault()?.MatchedTokens ?? [];
        var matchedWeight = queryTerms.Count(t => matched.Contains(t, StringComparer.Ordinal)) +
            expanded.Where(t => t.Weight < 1 && matched.Contains(t.Text, StringComparer.Ordinal)).Sum(t => t.Weight);
        var coverage = queryTerms.Length == 0 ? 0 : Math.Min(1, matchedWeight / queryTerms.Length);
        var score = ConfidenceScorer.Score(hits, options.K, coverage);
        return new(normalized, language, tokens, expanded, hits, score.Confidence, score.Margin,
            AnswerDecision.Decide(score.Confidence, score.Margin, options));
    }
}
