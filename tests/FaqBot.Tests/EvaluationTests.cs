#pragma warning disable CA1707 // Test names describe method, scenario, and expected result.
using FaqBot.Core;

namespace FaqBot.Tests;

public sealed class EvaluationTests
{
    [Fact]
    public void Calculate_MixedPredictions_UsesCorrectDenominators()
    {
        EvaluationObservation[] rows =
        [
            Row("a", DecisionKind.Answer, "a"), Row("b", DecisionKind.Answer, "a"),
            Row("c", DecisionKind.Suggest, "a", "c"), Row("d", DecisionKind.Suggest, "d"),
            Row(null, DecisionKind.Fallback), Row(null, DecisionKind.Suggest, "a")
        ];
        var metrics = EvaluationCalculator.Calculate(rows);
        Assert.Equal(6, metrics.Total);
        Assert.Equal(4, metrics.InScope);
        Assert.Equal(2, metrics.CorrectTop1);
        Assert.Equal(.5, metrics.Top1Accuracy);
        Assert.Equal(2, metrics.Answered);
        Assert.Equal(1, metrics.CorrectAnswers);
        Assert.Equal(.5, metrics.AnswerPrecision);
        Assert.Equal(2, metrics.Suggested);
        Assert.Equal(2, metrics.CorrectSuggestions);
        Assert.Equal(1, metrics.SuggestRecall);
        Assert.Equal(2, metrics.OutOfScope);
        Assert.Equal(.5, metrics.FallbackRate);
    }
    [Fact]
    public void Calculate_AnswerOutsideTopThree_DoesNotCountAsCorrectSuggestion()
    {
        var metrics = EvaluationCalculator.Calculate([Row("d", DecisionKind.Suggest, "a", "b", "c", "d")]);
        Assert.Equal(0, metrics.SuggestRecall);
    }
    [Fact]
    public void Calculate_EmptyDataset_ReturnsZeroRatios()
    {
        var metrics = EvaluationCalculator.Calculate([]);
        Assert.Equal(0, metrics.Total);
        Assert.Equal(0, metrics.Top1Accuracy);
        Assert.Equal(0, metrics.AnswerPrecision);
        Assert.Equal(0, metrics.SuggestRecall);
        Assert.Equal(0, metrics.FallbackRate);
    }
    private static EvaluationObservation Row(string? expected, DecisionKind decision, params string[] hits) => new(
        new("sample", "en", expected), new("sample", new("en", 1), [], [], hits.Select(id => new SearchHit(id, 1, [])).ToArray(), .5, .1, decision));
}
