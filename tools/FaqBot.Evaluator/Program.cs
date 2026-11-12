using System.Globalization;
using System.Text.Json;
using FaqBot.Core;
using FaqBot.Infrastructure;

var dataset = "data/eval.jsonl";
var seed = "data/faq.seed.json";
var synonyms = "data/synonyms.json";
var minimum = .8;
var verbose = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--verbose") { verbose = true; continue; }
    if (i + 1 >= args.Length) { Console.Error.WriteLine("A value is required for " + args[i]); return 2; }
    switch (args[i])
    {
        case "--dataset": dataset = args[++i]; break;
        case "--seed": seed = args[++i]; break;
        case "--synonyms": synonyms = args[++i]; break;
        case "--min-accuracy":
            if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out minimum) || !double.IsFinite(minimum) || minimum is < 0 or > 1)
            { Console.Error.WriteLine("Minimum accuracy must be between 0 and 1."); return 2; }
            break;
        default: Console.Error.WriteLine("Unknown argument: " + args[i]); return 2;
    }
}
try
{
    var engine = FaqEngineFactory.CreateFromSeed(seed, synonyms);
    var cases = (await File.ReadAllLinesAsync(dataset, CancellationToken.None)).Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<EvaluationCase>(line, FaqJson.Options) ?? throw new JsonException("Invalid evaluation case.")).ToArray();
    if (cases.Length == 0 || cases.Any(c => string.IsNullOrWhiteSpace(c.Question) || !SupportedLocales.Contains(c.Locale)))
    { Console.Error.WriteLine("The dataset must contain questions with supported locales."); return 2; }
    var observations = cases.Select(c => new EvaluationObservation(c, engine.Search(c.Question, c.Locale))).ToArray();
    Console.WriteLine("Locale | Count | Top-1 accuracy | Answer precision | Suggest top-3 | OOS fallback");
    foreach (var locale in SupportedLocales.All.Append("ALL"))
    {
        var metrics = EvaluationCalculator.Calculate(observations.Where(o => locale == "ALL" || o.Case.Locale == locale));
        Console.WriteLine(FormattableString.Invariant($"{locale,-6} | {metrics.Total,5} | {metrics.Top1Accuracy,14:P1} | {metrics.AnswerPrecision,16:P1} | {metrics.SuggestRecall,13:P1} | {metrics.FallbackRate,12:P1}"));
    }
    if (verbose)
        foreach (var row in observations.Where(o => o.Case.ExpectedFaqId is null ? o.Result.Decision != DecisionKind.Fallback : o.Result.Hits.FirstOrDefault()?.FaqId != o.Case.ExpectedFaqId))
            Console.WriteLine(FormattableString.Invariant($"MISS [{row.Case.Locale}] {row.Case.Question} | expected={row.Case.ExpectedFaqId ?? "Fallback"} actual={row.Result.Hits.FirstOrDefault()?.FaqId ?? "none"} confidence={row.Result.Confidence:F3} decision={row.Result.Decision}"));
    var total = EvaluationCalculator.Calculate(observations);
    return total.Top1Accuracy >= minimum && (total.OutOfScope == 0 || total.FallbackRate >= .75) ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
{
    Console.Error.WriteLine("Evaluation failed: " + exception.Message);
    return 2;
}
