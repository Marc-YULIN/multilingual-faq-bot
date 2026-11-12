#pragma warning disable CA1707 // Test names describe method, scenario, and expected result.
using FaqBot.Core;
using FaqBot.Infrastructure;

namespace FaqBot.Tests;

public sealed class ChatTests
{
    [Fact]
    public async Task HandleAsync_DirectMatch_ReturnsFaqAndWritesLog()
    {
        var rig = CreateRig(new() { K = .01 });
        var response = await rig.Handler.HandleAsync(new("demo", "refund payment parcel", "en"), CancellationToken.None);
        Assert.Equal("faq", response.Source);
        Assert.Equal("refund", response.FaqId);
        Assert.Equal("A verified answer.", response.Reply);
        Assert.Equal(0, rig.Llm.Calls);
        Assert.Equal(rig.Time.GetUtcNow(), Assert.Single(rig.Repository.Logs).CreatedAt);
    }
    [Fact]
    public async Task HandleAsync_ModerateConfidence_ReturnsSuggestions()
    {
        var rig = CreateRig(new() { K = .01, AnswerThreshold = 1, SuggestThreshold = .1 });
        var response = await rig.Handler.HandleAsync(new("demo", "refund payment parcel", "en"), CancellationToken.None);
        Assert.Equal("suggestions", response.Source);
        Assert.Equal("refund", Assert.Single(response.Suggestions).FaqId);
        Assert.Equal(0, rig.Llm.Calls);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_LowConfidenceWithLlm_ReturnsGroundedAnswer()
    {
        var rig = CreateRig(FallbackOptions());
        var response = await rig.Handler.HandleAsync(new("demo", "refund", "en"), CancellationToken.None);
        Assert.Equal("llm", response.Source);
        Assert.Equal("Grounded answer", response.Reply);
        var context = Assert.Single(rig.Llm.LastRequest!.Context);
        Assert.Equal("refund", context.FaqId);
        Assert.Equal("A verified answer.", context.Answer);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_LowConfidenceWithLlm_PassesNormalizedConfidenceNotRawScore()
    {
        var rig = CreateRig(FallbackOptions());
        var response = await rig.Handler.HandleAsync(new("demo", "refund", "en"), CancellationToken.None);
        Assert.InRange(rig.Llm.LastRequest!.TopConfidence, 0, 1);
        Assert.Equal(response.Confidence, rig.Llm.LastRequest.TopConfidence, 6);
    }
    [Theory]
    [InlineData(true, "HANDOFF")]
    [InlineData(false, " HANDOFF ")]
    [InlineData(false, " ")]
    public async Task HandleAsync_LlmDeclines_ReturnsLocalizedHandoff(bool flag, string text)
    {
        var rig = CreateRig(FallbackOptions());
        rig.Llm.Complete = _ => Task.FromResult(new LlmResponse(text, flag));
        var response = await rig.Handler.HandleAsync(new("demo", "無法處理的問題", "zh-TW"), CancellationToken.None);
        Assert.Equal("handoff", response.Source);
        Assert.Contains("客服", response.Reply, StringComparison.Ordinal);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_LlmThrows_ReturnsHandoff()
    {
        var rig = CreateRig(FallbackOptions());
        rig.Llm.Complete = _ => throw new HttpRequestException("Service unavailable.");
        Assert.Equal("handoff", (await rig.Handler.HandleAsync(new("demo", "unknown", "en"), CancellationToken.None)).Source);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_LlmTimesOut_ReturnsHandoffAfterEightSeconds()
    {
        var rig = CreateRig(FallbackOptions());
        rig.Llm.Complete = async token => { await Task.Delay(Timeout.Infinite, token); return new("late", false); };
        var pending = rig.Handler.HandleAsync(new("demo", "unknown", "en"), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        rig.Time.Advance(TimeSpan.FromSeconds(8));
        var response = await pending;
        Assert.Equal("handoff", response.Source);
        Assert.Equal(8000, response.LatencyMs);
    }
    [Fact]
    public async Task HandleAsync_CallerCancels_PropagatesCancellation()
    {
        var rig = CreateRig(FallbackOptions());
        rig.Llm.Complete = async token => { await Task.Delay(Timeout.Infinite, token); return new("late", false); };
        using var cancellation = new CancellationTokenSource();
        var pending = rig.Handler.HandleAsync(new("demo", "unknown", "en"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_DisabledSite_SkipsRetrievalAndLlm()
    {
        var rig = CreateRig(new());
        rig.Repository.Site = rig.Repository.Site! with { Enabled = false };
        Assert.Equal("disabled", (await rig.Handler.HandleAsync(new("demo", "refund", "en"), CancellationToken.None)).Source);
        Assert.Equal(0, rig.Indexes.Calls);
        Assert.Equal(0, rig.Llm.Calls);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_LlmDisabled_UsesConfiguredHandoffWithoutLlm()
    {
        var rig = CreateRig(FallbackOptions());
        rig.Repository.Site = rig.Repository.Site! with { LlmFallbackEnabled = false, HandoffMessages = new() { ["en"] = "Contact the store." } };
        var response = await rig.Handler.HandleAsync(new("demo", "unknown", "en"), CancellationToken.None);
        Assert.Equal("handoff", response.Source);
        Assert.Equal("Contact the store.", response.Reply);
        Assert.Equal(0, rig.Llm.Calls);
        Assert.Single(rig.Repository.Logs);
    }
    [Fact]
    public async Task HandleAsync_RepeatedNormalizedQuestion_UsesCacheAndLogsEveryRequest()
    {
        var rig = CreateRig(new() { K = .01 });
        var cache = new CachedChatOrchestrator(rig.Handler, rig.Repository, rig.Repository, rig.Time);
        await cache.HandleAsync(new("demo", "refund payment parcel", "en", "first"), CancellationToken.None);
        var second = new ChatRequest("demo", "ＲＥＦＵＮＤ payment parcel!", "en", "second");
        Assert.True(await cache.IsCachedAsync(second, CancellationToken.None));
        Assert.Equal("faq", (await cache.HandleAsync(second, CancellationToken.None)).Source);
        Assert.Equal(1, rig.Indexes.Calls);
        Assert.Equal(2, rig.Repository.Logs.Count);
        Assert.Equal("second", rig.Repository.Logs[1].Request.SessionId);
        Assert.Equal(second.Message, rig.Repository.Logs[1].Request.Message);
    }
    [Fact]
    public async Task HandleAsync_KnowledgeVersionChanges_InvalidatesCache()
    {
        var rig = CreateRig(new() { K = .01 });
        var cache = new CachedChatOrchestrator(rig.Handler, rig.Repository, rig.Repository, rig.Time);
        var request = new ChatRequest("demo", "refund payment parcel", "en");
        await cache.HandleAsync(request, CancellationToken.None);
        rig.Repository.Version++;
        Assert.False(await cache.IsCachedAsync(request, CancellationToken.None));
        await cache.HandleAsync(request, CancellationToken.None);
        Assert.Equal(2, rig.Indexes.Calls);
    }
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 2)]
    public async Task HandleAsync_CacheDurationExpires_RecomputesResponse(bool llm, int minutes)
    {
        var rig = CreateRig(llm ? FallbackOptions() : new() { K = .01 });
        var cache = new CachedChatOrchestrator(rig.Handler, rig.Repository, rig.Repository, rig.Time);
        var request = new ChatRequest("demo", "refund payment parcel", "en");
        await cache.HandleAsync(request, CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromTicks(1));
        Assert.True(await cache.IsCachedAsync(request, CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromTicks(1));
        Assert.False(await cache.IsCachedAsync(request, CancellationToken.None));
        await cache.HandleAsync(request, CancellationToken.None);
        Assert.Equal(2, rig.Indexes.Calls);
    }
    [Fact]
    public async Task HandleAsync_HandoffResponse_DoesNotCache()
    {
        var rig = CreateRig(FallbackOptions());
        rig.Llm.Complete = _ => Task.FromResult(new LlmResponse("HANDOFF", true));
        var cache = new CachedChatOrchestrator(rig.Handler, rig.Repository, rig.Repository, rig.Time);
        var request = new ChatRequest("demo", "unknown", "en");
        await cache.HandleAsync(request, CancellationToken.None);
        Assert.False(await cache.IsCachedAsync(request, CancellationToken.None));
        await cache.HandleAsync(request, CancellationToken.None);
        Assert.Equal(2, rig.Llm.Calls);
        Assert.Equal(2, rig.Repository.Logs.Count);
    }
    private static RetrievalOptions FallbackOptions() => new() { AnswerThreshold = 1, SuggestThreshold = 1 };
    private static Rig CreateRig(RetrievalOptions options)
    {
        var repository = new MemoryRepository();
        var indexes = new FixedIndexes(new(repository.Items, [], options));
        var llm = new StubLlm();
        var time = new ManualTimeProvider();
        return new(repository, indexes, llm, time, new(repository, indexes, llm, repository, options, time));
    }
    private sealed record Rig(MemoryRepository Repository, FixedIndexes Indexes, StubLlm Llm, ManualTimeProvider Time, ChatOrchestrator Handler);
}
