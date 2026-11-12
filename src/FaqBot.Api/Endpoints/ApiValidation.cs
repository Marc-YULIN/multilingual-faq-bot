using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using FaqBot.Core;
using Microsoft.Extensions.Options;

namespace FaqBot.Api.Endpoints;

public sealed class AdminOptions { public string Key { get; set; } = ""; }
public sealed class AdminKeyFilter(IOptions<AdminOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var supplied = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
        var configured = options.Value.Key;
        if (string.IsNullOrEmpty(configured) || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
            return ApiValidation.Problem(401, "admin_key_required", "A valid admin key is required.");
        return await next(context);
    }
}
public static class ApiValidation
{
    public static IResult Problem(int status, string code, string detail) => Results.Problem(statusCode: status, detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
    public static bool Valid(ChatRequest? request) => request is not null && !string.IsNullOrWhiteSpace(request.SiteCode) &&
        request.SiteCode.Length <= 64 && !string.IsNullOrWhiteSpace(request.Message) && request.Message.Length <= 2000 &&
        (request.SessionId is null || request.SessionId.Length <= 128);
    public static bool Valid(FaqItem? item) => item is not null && !string.IsNullOrWhiteSpace(item.Id) && item.Id.Length <= 128 &&
        !string.IsNullOrWhiteSpace(item.Category) && item.Category.Length <= 128 && item.Translations is not null &&
        SupportedLocales.All.All(locale => item.Translations.TryGetValue(locale, out var t) && t is not null &&
            !string.IsNullOrWhiteSpace(t.Question) && t.Question.Length <= 2000 && !string.IsNullOrWhiteSpace(t.Answer) &&
            t.Answer.Length <= 10000 && t.AlternateQuestions is not null && t.Keywords is not null &&
            t.AlternateQuestions.Length <= 100 && t.Keywords.Length <= 100 &&
            t.AlternateQuestions.Concat(t.Keywords).All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 2000));
    public static bool Valid(SiteSetting? site) => site is not null && !string.IsNullOrWhiteSpace(site.SiteCode) &&
        site.SiteCode.Length <= 64 && SupportedLocales.Contains(site.DefaultLocale) && site.HandoffMessages is not null &&
        site.HandoffMessages.All(p => SupportedLocales.Contains(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && p.Value.Length <= 2000);
}
