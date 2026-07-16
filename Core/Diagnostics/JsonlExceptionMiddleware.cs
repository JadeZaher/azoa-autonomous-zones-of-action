using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>Logs completed error responses without owning exception handling.</summary>
public sealed class JsonlExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JsonlExceptionMiddleware> _logger;

    public JsonlExceptionMiddleware(RequestDelegate next, ILogger<JsonlExceptionMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        if (context.Response.StatusCode >= 400)
        {
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["requestId"] = context.TraceIdentifier,
                ["requestMethod"] = context.Request.Method,
                ["requestPath"] = context.Request.Path.Value,
                ["statusCode"] = context.Response.StatusCode,
                ["TraceId"] = Activity.Current?.TraceId.ToString(),
                ["SpanId"] = Activity.Current?.SpanId.ToString(),
            }))
            {
                _logger.LogWarning(
                    "Pipeline response {StatusCode} for {Method} {Path} (requestId={RequestId})",
                    context.Response.StatusCode,
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier);
            }
        }
    }
}
