using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FaqBot.Core;

public static partial class TextNormalizer
{
    private const string Simplified = "号码货账户单运费发票会员级点过优惠券联络隐删资设验证双重认失败进查询取消时间电话开关闭密码订单退钱购买登录网页邮输错误余额用复务无储备变更机绑定应台万与为这来还从个后里现线实样详记载处状态选择填写确认请问";
    private const string Traditional = "號碼貨帳戶單運費發票會員級點過優惠券聯絡隱刪資設驗證雙重認失敗進查詢取消時間電話開關閉密碼訂單退錢購買登錄網頁郵輸錯誤餘額用復務無儲備變更機綁定應臺萬與為這來還從個後裡現線實樣詳記載處狀態選擇填寫確認請問";
    public static string Normalize(string text, string locale)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var output = new StringBuilder();
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.OtherLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
                UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                var index = locale == "zh-TW" && rune.IsBmp ? Simplified.IndexOf((char)rune.Value) : -1;
                output.Append(index >= 0 ? Traditional[index].ToString() : rune.ToString());
            }
            else output.Append(' ');
        }
        return Spaces().Replace(output.ToString(), " ").Trim();
    }
    public static string RemoveDiacritics(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) output.Append(c == 'đ' ? 'd' : c);
        return output.ToString().Normalize(NormalizationForm.FormC);
    }
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

public static class LanguageDetector
{
    public static LanguageResult Detect(string text, string? hint = null)
    {
        if (SupportedLocales.Contains(hint)) return new(hint!, 1);
        var letters = text.Normalize(NormalizationForm.FormC).Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return new("en", .2);
        var hangul = letters.Count(c => c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff' or >= '\u3130' and <= '\u318f');
        var cjk = letters.Count(c => c is >= '\u3400' and <= '\u9fff');
        if (hangul > 0 && hangul >= cjk) return new("ko", .5 + .5 * hangul / letters.Length);
        if (cjk > 0) return new("zh-TW", .5 + .5 * cjk / letters.Length);
        if (letters.Any(c => "ăâđêôơưĂÂĐÊÔƠƯáàảãéèẻẽíìỉĩóòỏõúùủũýỳỷỹÁÀÃÉÈÍÌÓÒÕÚÙÝ".Contains(c) || c is >= '\u1ea0' and <= '\u1ef9')) return new("vi", .9);
        return new("en", .8);
    }
}

public static class Tokenizer
{
    private static readonly HashSet<string> Stopwords = new("a an the is are am was were be been being do does did can could would should how what when where why who i me my you your we our they their it its to of for on in at and or with please want need have has get".Split(' '), StringComparer.Ordinal);
    private static readonly string[] KoreanSuffixes = ["에서", "으로", "은", "는", "이", "가", "을", "를", "에", "로", "도", "요"];
    public static string[] Tokenize(string normalized, string locale)
    {
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return locale switch
        {
            "zh-TW" => words.SelectMany(w => Ngrams(w, 1).Where(t => !"的了嗎呢吧我你要想請問".Contains(t, StringComparison.Ordinal)).Concat(Ngrams(w, 2))).ToArray(),
            "ko" => words.SelectMany(w => new[] { w, StripKorean(w) }.Concat(Ngrams(w, 2))).ToArray(),
            "vi" => words.Concat(words.Zip(words.Skip(1), (a, b) => a + " " + b)).SelectMany(w => new[] { w, TextNormalizer.RemoveDiacritics(w) }.Distinct(StringComparer.Ordinal)).ToArray(),
            _ => words.Where(w => !Stopwords.Contains(w)).Select(Stem).Where(w => w.Length > 0).ToArray()
        };
    }
    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "s" })
            if (word.Length > suffix.Length + 3 && word.EndsWith(suffix, StringComparison.Ordinal)) return word[..^suffix.Length];
        return word;
    }
    private static string StripKorean(string word)
    {
        foreach (var suffix in KoreanSuffixes)
            if (word.Length > suffix.Length + 1 && word.EndsWith(suffix, StringComparison.Ordinal)) return word[..^suffix.Length];
        return word;
    }
    private static IEnumerable<string> Ngrams(string word, int size)
    {
        for (var i = 0; i <= word.Length - size; i++) yield return word.Substring(i, size);
    }
}

public sealed class SynonymExpander(Dictionary<string, string[][]> groups)
{
    public QueryToken[] Expand(string normalized, string locale, string[] tokens)
    {
        var expanded = tokens.Distinct(StringComparer.Ordinal).ToDictionary(t => t, _ => 1d, StringComparer.Ordinal);
        if (groups.TryGetValue(locale, out var localeGroups))
            foreach (var group in localeGroups)
            {
                var phrases = group.Select(p => TextNormalizer.Normalize(p, locale)).ToArray();
                if (!phrases.Any(p => locale == "zh-TW" || locale == "ko"
                        ? normalized.Contains(p, StringComparison.Ordinal)
                        : (" " + normalized + " ").Contains(" " + p + " ", StringComparison.Ordinal))) continue;
                foreach (var token in phrases.SelectMany(p => Tokenizer.Tokenize(p, locale))) expanded.TryAdd(token, .5);
            }
        return expanded.Select(t => new QueryToken(t.Key, t.Value)).ToArray();
    }
}
