#pragma warning disable CA1707 // Test names describe method, scenario, and expected result.
using FaqBot.Core;

namespace FaqBot.Tests;

public sealed class RetrievalTests
{
    [Theory]
    [InlineData("ＡＰＰ  ＬＯＧＩＮ！ 😊", "en", "app login")]
    [InlineData("账号号码、密码，退货", "zh-TW", "帳號號碼 密碼 退貨")]
    [InlineData("  Hoàn   tiền! ", "vi", "hoàn tiền")]
    public void Normalize_WidthPunctuationAndScripts_ReturnsNormalizedText(string input, string locale, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input, locale));
    [Fact]
    public void RemoveDiacritics_VietnameseText_PreservesPlainLetters() => Assert.Equal("doi mat khau", TextNormalizer.RemoveDiacritics("đổi mật khẩu"));
    [Theory]
    [InlineData("請問退款", null, "zh-TW")]
    [InlineData("환불 문의", null, "ko")]
    [InlineData("đổi mật khẩu", null, "vi")]
    [InlineData("refund please", null, "en")]
    [InlineData("APP 로그인", null, "ko")]
    [InlineData("APP 請問", null, "zh-TW")]
    [InlineData("refund please", "vi", "vi")]
    [InlineData("請問退款", "invalid", "zh-TW")]
    [InlineData("123!?", null, "en")]
    public void Detect_ScriptsAndHints_ReturnsExpectedLocale(string input, string? hint, string expected)
    {
        var result = LanguageDetector.Detect(input, hint);
        Assert.Equal(expected, result.Locale);
        Assert.InRange(result.Confidence, 0, 1);
        if (hint == expected) Assert.Equal(1, result.Confidence);
    }
    [Theory]
    [InlineData("the payments are failing", "en", "payment")]
    [InlineData("we cancelled orders", "en", "order")]
    [InlineData("我的退款", "zh-TW", "退款")]
    [InlineData("請問退貨嗎", "zh-TW", "貨")]
    [InlineData("환불은 어디", "ko", "환불")]
    [InlineData("배송에서 조회", "ko", "배송")]
    [InlineData("hoàn tiền", "vi", "hoan tien")]
    [InlineData("đổi mật khẩu", "vi", "mật khẩu")]
    public void Tokenize_FourLocales_ProducesExpectedTerms(string input, string locale, string expected) => Assert.Contains(expected, Tokenizer.Tokenize(input, locale));
    [Fact]
    public void Tokenize_EnglishStopwords_RemovesFunctionWords()
    {
        var tokens = Tokenizer.Tokenize("how can i get the refunds", "en");
        Assert.Equal(["refund"], tokens);
    }
    [Fact]
    public void Tokenize_ChineseStopwords_RemovesUnigrams() => Assert.DoesNotContain("我", Tokenizer.Tokenize("我退款", "zh-TW"));
    [Fact]
    public void Expand_MatchingSynonym_AddsHalfWeightedTerms()
    {
        var expander = new SynonymExpander(new() { ["en"] = [["refund", "money back", "reimburse"]] });
        var expanded = expander.Expand("refund", "en", ["refund"]);
        Assert.Equal(1, Assert.Single(expanded, t => t.Text == "refund").Weight);
        Assert.Equal(.5, Assert.Single(expanded, t => t.Text == "reimburse").Weight);
        Assert.Contains(expanded, t => t.Text == "money" && t.Weight == .5);
        Assert.DoesNotContain(expander.Expand("unrelated", "en", ["unrelated"]), t => t.Text == "refund");
    }
    [Fact]
    public void Expand_WordSubstring_DoesNotExpandUnrelatedEnglishWord()
    {
        var expanded = new SynonymExpander(new() { ["en"] = [["pay", "payment"]] }).Expand("repay", "en", ["repay"]);
        Assert.DoesNotContain(expanded, t => t.Text == "payment");
    }
    [Fact]
    public void Search_RelevantAndDisabledDocuments_RanksRelevantEnabledFirst()
    {
        var index = new Bm25Index([TestData.Item("refund", "refund payment"), TestData.Item("delivery", "shipping delivery"), TestData.Item("hidden", "refund", false)], "en");
        var result = index.Search([new("refund")]);
        Assert.Equal("refund", Assert.Single(result).FaqId);
        Assert.Contains("refund", result[0].MatchedTokens);
    }
    [Fact]
    public void Search_RareAndCommonTerms_GivesRareTermHigherIdf()
    {
        var index = new Bm25Index([TestData.Item("a", "common unique"), TestData.Item("b", "common ordinary"), TestData.Item("c", "common regular")], "en");
        var rare = Assert.Single(index.Search([new("unique")])).Score;
        var common = index.Search([new("common")]).Single(h => h.FaqId == "a").Score;
        Assert.True(rare > common);
    }
    [Fact]
    public void Score_MarginCoverageAndNoHits_ProducesBoundedConfidence()
    {
        var isolated = ConfidenceScorer.Score([new("a", 4, [])]);
        Assert.Equal(.5, isolated.Confidence, 8);
        Assert.Equal(1, isolated.Margin);
        Assert.True(ConfidenceScorer.Score([new("a", 4, []), new("b", 4, [])]).Confidence < isolated.Confidence);
        Assert.Equal(isolated.Confidence / 2, ConfidenceScorer.Score([new("a", 4, [])], coverage: .5).Confidence, 8);
        Assert.Equal(new ConfidenceResult(0, 0), ConfidenceScorer.Score([]));
    }
    [Theory]
    [InlineData(.62, .08, DecisionKind.Answer)]
    [InlineData(.62, .079, DecisionKind.Suggest)]
    [InlineData(.619, .08, DecisionKind.Suggest)]
    [InlineData(.35, .0, DecisionKind.Suggest)]
    [InlineData(.349, 1, DecisionKind.Fallback)]
    public void Decide_ThresholdBoundaries_ReturnsExpectedDecision(double confidence, double margin, DecisionKind expected) =>
        Assert.Equal(expected, AnswerDecision.Decide(confidence, margin, new()));
    [Fact]
    public void Tokenize_RepeatedVietnameseTerms_PreservesDocumentFrequency()
    {
        var tokens = Tokenizer.Tokenize("hoàn hoàn", "vi");
        Assert.Equal(2, tokens.Count(t => t == "hoàn"));
        Assert.Equal(2, tokens.Count(t => t == "hoan"));
    }
    [Fact]
    public void Search_SynonymOnlyQuery_RetainsEvidenceConfidence()
    {
        var engine = new FaqEngine([TestData.Item("refund", "refund payment")], new() { ["en"] = [["refund", "reimburse"]] }, new() { K = .01 });
        var result = engine.Search("reimburse", "en");
        Assert.Equal("refund", result.Hits[0].FaqId);
        Assert.True(result.Confidence > 0);
        Assert.Contains(result.ExpandedTokens, t => t.Text == "refund" && t.Weight == .5);
    }}
