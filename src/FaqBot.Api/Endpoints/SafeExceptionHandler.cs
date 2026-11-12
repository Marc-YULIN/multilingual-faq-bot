using Microsoft.AspNetCore.Diagnostics;

namespace FaqBot.Api.Endpoints;

public sealed partial class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        RequestFailed(logger, exception.GetType().Name);
        httpContext.Response.StatusCode = exception is BadHttpRequestException ? 400 : 500;
        return await problems.TryWriteAsync(new() { HttpContext = httpContext, ProblemDetails = new()
        {
            Status = httpContext.Response.StatusCode, Title = "The request could not be completed.",
            Extensions = { ["code"] = httpContext.Response.StatusCode == 400 ? "invalid_request" : "request_failed" }
        } });
    }
    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed with exception type {ExceptionType}")]
    private static partial void RequestFailed(ILogger logger, string exceptionType);
}
public static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Help center initialized with local retrieval and versioned knowledge base")]
    public static partial void Initialized(ILogger logger);
}
