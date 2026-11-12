using System.Text.Json;
using FaqBot.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FaqBot.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddFaqBotInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<DataOptions>().BindConfiguration("Data").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RetrievalOptions>().BindConfiguration("Retrieval").ValidateDataAnnotations()
            .Validate(o => o.AnswerThreshold >= o.SuggestThreshold, "AnswerThreshold must be at least SuggestThreshold.").ValidateOnStart();
        services.AddOptions<LlmOptions>().BindConfiguration("Llm").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<GeminiOptions>().BindConfiguration("Llm:Gemini")
            .PostConfigure(o => o.ApiKey = string.IsNullOrWhiteSpace(o.ApiKey) ? Environment.GetEnvironmentVariable("GEMINI_API_KEY") : o.ApiKey)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RetrievalOptions>>().Value);
        services.AddDbContextFactory<FaqBotDbContext>(o => o.UseSqlite(configuration.GetConnectionString("FaqBot") ?? "Data Source=appdata/multilingual-faq-bot.db"));
        services.AddSingleton<FaqRepository>();
        services.AddSingleton<IFaqRepository>(sp => sp.GetRequiredService<FaqRepository>());
        services.AddSingleton<IChatLogWriter>(sp => sp.GetRequiredService<FaqRepository>());
        services.AddSingleton<IFaqIndexProvider, FaqIndexProvider>();
        services.AddSingleton<FaqSeeder>();
        services.AddHttpClient<GeminiLlmClient>(http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<ILlmClient>(sp =>
        {
            var provider = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider;
            var key = sp.GetRequiredService<IOptions<GeminiOptions>>().Value.ApiKey;
            return provider switch
            {
                "Disabled" => new DisabledLlmClient(),
                "Gemini" => sp.GetRequiredService<GeminiLlmClient>(),
                "Auto" when !string.IsNullOrWhiteSpace(key) => sp.GetRequiredService<GeminiLlmClient>(),
                _ => new ExtractiveFallbackLlmClient()
            };
        });
        services.AddSingleton<ChatOrchestrator>();
        services.AddSingleton<CachedChatOrchestrator>();
        services.AddSingleton<IChatHandler>(sp => sp.GetRequiredService<CachedChatOrchestrator>());
        return services;
    }
}
public static class FaqEngineFactory
{
    public static FaqEngine CreateFromSeed(string seedPath, string synonymsPath, RetrievalOptions? options = null)
    {
        var items = JsonSerializer.Deserialize<FaqItem[]>(File.ReadAllText(seedPath), FaqJson.Options) ?? [];
        var synonyms = JsonSerializer.Deserialize<Dictionary<string, string[][]>>(File.ReadAllText(synonymsPath), FaqJson.Options) ?? [];
        return new(items, synonyms, options ?? new());
    }
}
