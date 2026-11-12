#pragma warning disable CA1707 // Test names describe method, scenario, and expected result.
using System.Net;
using System.Text;
using System.Text.Json;
using FaqBot.Core;
using FaqBot.Infrastructure;
using Microsoft.Extensions.Options;

namespace FaqBot.Tests;

public sealed class LlmTests
{
    [Fact]
    public async Task CompleteAsync_ValidResponse_SendsGroundedRequestAndParsesAnswer()
    {
        var inspected = false;
        using var handler = new StubHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://example.invalid/v1beta/models/demo-model:generateContent", message.RequestUri!.ToString());
            Assert.Equal("sample-key", Assert.Single(message.Headers.GetValues("x-goog-api-key")));
            using var document = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            var rules = root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
            Assert.Contains("only", rules, StringComparison.Ordinal);
            Assert.Contains("HANDOFF", rules, StringComparison.Ordinal);
            Assert.Contains("en", rules, StringComparison.Ordinal);
            Assert.Equal(.2, root.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
            var contents = root.GetProperty("contents")[0];
            Assert.Equal("user", contents.GetProperty("role").GetString());
            using var context = JsonDocument.Parse(contents.GetProperty("parts")[0].GetProperty("text").GetString()!);
            Assert.Equal("refund?", context.RootElement.GetProperty("question").GetString());
            Assert.Equal("Refund in seven days.", context.RootElement.GetProperty("faqContext")[0].GetProperty("answer").GetString());
            inspected = true;
            return JsonResponse("""{"candidates":[{"content":{"parts":[{"text":"Refund in "},{"text":"seven days."}]}}]}""");
        });
        using var http = new HttpClient(handler);
        var client = CreateClient(http, new ManualTimeProvider());
        var result = await client.CompleteAsync(Request(), CancellationToken.None);
        Assert.True(inspected);
        Assert.False(result.IsHandoff);
        Assert.Equal("Refund in seven days.", result.Text);
    }
    [Theory]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"  HANDOFF \n"}]}}]}""")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""")]
    public async Task CompleteAsync_DeclinedOrBlockedResponse_ReturnsHandoff(string json)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(json)));
        using var http = new HttpClient(handler);
        var response = await CreateClient(http, new ManualTimeProvider()).CompleteAsync(Request(), CancellationToken.None);
        Assert.True(response.IsHandoff);
    }
    [Fact]
    public async Task CompleteAsync_RequestExceedsEightSeconds_CancelsStubAndReturnsHandoff()
    {
        var time = new ManualTimeProvider();
        using var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return JsonResponse("{}");
        });
        using var http = new HttpClient(handler);
        var pending = CreateClient(http, time).CompleteAsync(Request(), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.True((await pending).IsHandoff);
    }
    [Fact]
    public async Task CompleteAsync_CallerCancels_PropagatesCancellation()
    {
        using var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return JsonResponse("{}"); });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = CreateClient(http, new ManualTimeProvider()).CompleteAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
    [Fact]
    public async Task CompleteAsync_NoKey_DoesNotSendHttpRequest()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Network access is forbidden in this test."));
        using var http = new HttpClient(handler);
        var client = new GeminiLlmClient(http, Options.Create(new GeminiOptions()), new ManualTimeProvider());
        Assert.True((await client.CompleteAsync(Request(), CancellationToken.None)).IsHandoff);
    }
    [Fact]
    public async Task CompleteAsync_OfflineProvider_QuotesContextAndDeclinesWithoutEvidence()
    {
        var client = new ExtractiveFallbackLlmClient();
        var answer = await client.CompleteAsync(Request(), CancellationToken.None);
        Assert.False(answer.IsHandoff);
        Assert.Contains("offline simulation", answer.Text, StringComparison.Ordinal);
        Assert.Contains("Refund in seven days.", answer.Text, StringComparison.Ordinal);
        Assert.True((await client.CompleteAsync(Request() with { Context = [] }, CancellationToken.None)).IsHandoff);
        Assert.True((await client.CompleteAsync(Request() with { TopConfidence = .01 }, CancellationToken.None)).IsHandoff);
    }
    private static GeminiLlmClient CreateClient(HttpClient http, TimeProvider time) => new(http,
        Options.Create(new GeminiOptions { ApiKey = "sample-key", Model = "demo-model", Endpoint = "https://example.invalid" }), time);
    private static LlmRequest Request() => new("refund?", "en", [new("refund", "When is a refund ready?", "Refund in seven days.")], 1, .35);
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
