using FaqBot.Api.Endpoints;
using FaqBot.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("code", "http_" + context.HttpContext.Response.StatusCode));
builder.Services.AddExceptionHandler<SafeExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddOptions<AdminOptions>().BindConfiguration("Admin").Configure(options =>
{
    if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(options.Key)) options.Key = "dev-admin-key";
}).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddFaqBotInfrastructure(builder.Configuration);
builder.Services.PostConfigure<DataOptions>(options =>
{
    options.SeedPath = ResolveDataPath(options.SeedPath, builder.Environment.ContentRootPath);
    options.SynonymsPath = ResolveDataPath(options.SynonymsPath, builder.Environment.ContentRootPath);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapChat();
app.MapFaqs();
app.MapSites();
app.MapAnalytics();
await app.Services.GetRequiredService<FaqSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
StartupLog.Initialized(app.Logger);
await app.RunAsync();

static string ResolveDataPath(string path, string root)
{
    if (Path.IsPathRooted(path)) return path;
    var contentPath = Path.GetFullPath(Path.Combine(root, path));
    if (File.Exists(contentPath)) return contentPath;
    var repositoryPath = Path.GetFullPath(Path.Combine(root, "../..", path));
    return File.Exists(repositoryPath) ? repositoryPath : contentPath;
}

public partial class Program;
