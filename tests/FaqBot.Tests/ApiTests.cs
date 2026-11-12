#pragma warning disable CA1707 // Test names describe method, scenario, and expected result.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaqBot.Core;
using FaqBot.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FaqBot.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData("zh-TW")]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("vi")]
    public async Task Chat_ExactQuestionInEachLocale_ReturnsDirectFaq(string locale)
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var item = ReadSeed()[0];
        using var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("demo", item.Translations[locale].Question, locale), CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<ChatResponse>(CancellationToken.None);
        Assert.NotNull(reply);
        Assert.Equal("faq", reply.Source);
        Assert.Equal(item.Id, reply.FaqId);
        Assert.Equal(locale, reply.Locale);
        Assert.Equal(item.Translations[locale].Answer, reply.Reply);
    }
    [Fact]
    public async Task Diagnose_NoAdminKey_ReturnsUnauthorizedProblem()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/chat/diagnose?q=refund", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("admin_key_required", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
    [Fact]
    public async Task Import_AfterIndexAndCacheWarmup_ImmediatelyRetrievesNewFaq()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        const string question = "quartz cobalt lantern";
        using var beforeResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("docs", question, "en"), CancellationToken.None);
        beforeResponse.EnsureSuccessStatusCode();
        var before = await beforeResponse.Content.ReadFromJsonAsync<ChatResponse>(CancellationToken.None);
        Assert.Equal("handoff", before!.Source);
        client.DefaultRequestHeaders.Add("X-Admin-Key", "dev-admin-key");
        var imported = TestData.Item("support.lantern", question);
        using var import = await client.PostAsJsonAsync("/api/faqs/import", new[] { imported }, CancellationToken.None);
        import.EnsureSuccessStatusCode();
        using var afterResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("docs", question, "en"), CancellationToken.None);
        afterResponse.EnsureSuccessStatusCode();
        var after = await afterResponse.Content.ReadFromJsonAsync<ChatResponse>(CancellationToken.None);
        Assert.Equal("faq", after!.Source);
        Assert.Equal(imported.Id, after.FaqId);
        Assert.Equal("A verified answer.", after.Reply);
    }
    [Fact]
    public async Task Import_UpdatedFaq_InvalidatesExistingAnswerCache()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var item = ReadSeed()[0];
        var question = item.Translations["en"].Question;
        using var firstResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("demo", question, "en"), CancellationToken.None);
        firstResponse.EnsureSuccessStatusCode();
        item.Translations["en"] = item.Translations["en"] with { Answer = "Updated local help content." };
        client.DefaultRequestHeaders.Add("X-Admin-Key", "dev-admin-key");
        using var update = await client.PostAsJsonAsync("/api/faqs/import", new[] { item }, CancellationToken.None);
        update.EnsureSuccessStatusCode();
        using var afterResponse = await client.PostAsJsonAsync("/api/chat", new ChatRequest("demo", question, "en"), CancellationToken.None);
        var after = await afterResponse.Content.ReadFromJsonAsync<ChatResponse>(CancellationToken.None);
        Assert.Equal("Updated local help content.", after!.Reply);
    }
    [Fact]
    public async Task Chat_InvalidRequest_ReturnsValidationProblem()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/chat", new ChatRequest("demo", ""), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("invalid_chat", problem.RootElement.GetProperty("code").GetString());
    }
    private static FaqItem[] ReadSeed() => JsonSerializer.Deserialize<FaqItem[]>(File.ReadAllText(Path.Combine(TestData.Root, "data/faq.seed.json")), FaqJson.Options)!;
    private sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "faq-tests-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(folder);
            builder.UseEnvironment("Development").UseContentRoot(Path.Combine(TestData.Root, "src/FaqBot.Api"));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FaqBot"] = "Data Source=" + Path.Combine(folder, "test.db") + ";Pooling=False",
                ["Data:SeedPath"] = Path.Combine(TestData.Root, "data/faq.seed.json"),
                ["Data:SynonymsPath"] = Path.Combine(TestData.Root, "data/synonyms.json"),
                ["Llm:Provider"] = "Extractive",
                ["Admin:Key"] = "dev-admin-key"
            }));
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
