namespace FaqBot.Core;

public sealed class ChatOrchestrator(IFaqRepository repository, IFaqIndexProvider indexes,
    ILlmClient llm, IChatLogWriter logs, RetrievalOptions options, TimeProvider time) : IChatHandler
{
    public async Task<ChatResponse> HandleAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var site = await repository.GetSiteAsync(request.SiteCode, cancellationToken);
        var language = LanguageDetector.Detect(request.Message, request.Locale);
        ChatResponse response;
        if (site is null || !site.Enabled) response = new("This help center is currently unavailable.", language.Locale, "disabled", null, 0, [], 0);
        else
        {
            var engine = await indexes.GetAsync(cancellationToken);
            var result = engine.Search(request.Message, request.Locale);
            var contexts = result.Hits.Take(3).Select(hit =>
            {
                var t = engine.Items[hit.FaqId].Translations[language.Locale];
                return new LlmContext(hit.FaqId, t.Question, t.Answer);
            }).ToArray();
            response = new(Handoff(site, language.Locale), language.Locale, "handoff", null, result.Confidence, [], 0);
            if (result.Decision == DecisionKind.Answer && contexts.Length > 0)
                response = response with { Reply = contexts[0].Answer, Source = "faq", FaqId = contexts[0].FaqId };
            else if (result.Decision == DecisionKind.Suggest)
                response = response with { Reply = SuggestPrompt(language.Locale), Source = "suggestions", Suggestions = contexts.Select(c => new Suggestion(c.FaqId, c.Question)).ToArray() };
            else if (site.LlmFallbackEnabled)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8), time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    var completion = await llm.CompleteAsync(new(request.Message, language.Locale, contexts,
                        result.Confidence, options.SuggestThreshold), linked.Token)
                        .WaitAsync(TimeSpan.FromSeconds(8), time, cancellationToken);
                    if (!completion.IsHandoff && !string.IsNullOrWhiteSpace(completion.Text) && completion.Text.Trim() != "HANDOFF")
                        response = response with { Reply = completion.Text, Source = "llm" };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (TimeoutException) { }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        response = response with { LatencyMs = (long)time.GetElapsedTime(started).TotalMilliseconds };
        await logs.WriteAsync(new(request, response, time.GetUtcNow()), cancellationToken);
        return response;
    }
    private static string Handoff(SiteSetting site, string locale) => site.HandoffMessages.GetValueOrDefault(locale) ?? locale switch
    {
        "zh-TW" => "這個問題需要客服協助，請聯絡真人客服。",
        "ko" => "이 질문은 상담원의 도움이 필요합니다. 고객센터로 문의해 주세요.",
        "vi" => "Câu hỏi này cần nhân viên hỗ trợ. Vui lòng liên hệ bộ phận chăm sóc khách hàng.",
        _ => "Please contact a human support agent for help with this question."
    };
    private static string SuggestPrompt(string locale) => locale switch
    {
        "zh-TW" => "你是不是想問以下問題？", "ko" => "다음 질문을 찾으시나요?",
        "vi" => "Bạn có muốn hỏi một trong các câu sau không?", _ => "Did you mean one of these questions?"
    };
}
