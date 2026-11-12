namespace FaqBot.Core;

public sealed record EvaluationCase(string Question, string Locale, string? ExpectedFaqId);
public sealed record EvaluationObservation(EvaluationCase Case, RetrievalResult Result);
public sealed record EvaluationMetrics(int Total, int InScope, int CorrectTop1, int Answered, int CorrectAnswers,
    int Suggested, int CorrectSuggestions, int OutOfScope, int CorrectFallbacks)
{
    public double Top1Accuracy => Ratio(CorrectTop1, InScope);
    public double AnswerPrecision => Ratio(CorrectAnswers, Answered);
    public double SuggestRecall => Ratio(CorrectSuggestions, Suggested);
    public double FallbackRate => Ratio(CorrectFallbacks, OutOfScope);
    private static double Ratio(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}
public static class EvaluationCalculator
{
    public static EvaluationMetrics Calculate(IEnumerable<EvaluationObservation> observations)
    {
        var rows = observations.ToArray();
        var inScope = rows.Where(r => r.Case.ExpectedFaqId is not null).ToArray();
        var outOfScope = rows.Where(r => r.Case.ExpectedFaqId is null).ToArray();
        var answered = inScope.Where(r => r.Result.Decision == DecisionKind.Answer).ToArray();
        var suggested = inScope.Where(r => r.Result.Decision == DecisionKind.Suggest).ToArray();
        return new(rows.Length, inScope.Length,
            inScope.Count(r => r.Result.Hits.FirstOrDefault()?.FaqId == r.Case.ExpectedFaqId),
            answered.Length, answered.Count(r => r.Result.Hits.FirstOrDefault()?.FaqId == r.Case.ExpectedFaqId),
            suggested.Length, suggested.Count(r => r.Result.Hits.Take(3).Any(h => h.FaqId == r.Case.ExpectedFaqId)),
            outOfScope.Length, outOfScope.Count(r => r.Result.Decision == DecisionKind.Fallback));
    }
}
