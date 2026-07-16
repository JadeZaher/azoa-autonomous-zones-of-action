using System.Diagnostics;
using System.Text.Json;
using AZOA.WebAPI.Core.Diagnostics;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Middleware;

/// <summary>Logs and shapes unhandled request exceptions; see Core/Diagnostics/AGENTS.md.</summary>
public sealed class DebugExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<DebugExceptionMiddleware> _logger;

    public DebugExceptionMiddleware(RequestDelegate next, ILogger<DebugExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var activity = Activity.Current;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.AddEvent(new ActivityEvent(
                "exception",
                tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName },
                    { "exception.escaped", false },
                }));

            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["requestId"] = context.TraceIdentifier,
                ["requestMethod"] = context.Request.Method,
                ["requestPath"] = context.Request.Path.Value,
                ["statusCode"] = StatusCodes.Status500InternalServerError,
                ["TraceId"] = Activity.Current?.TraceId.ToString(),
                ["SpanId"] = Activity.Current?.SpanId.ToString(),
            }))
            {
                _logger.LogCritical(ex, "Unhandled exception for {Method} {Path}",
                    context.Request.Method, context.Request.Path);
            }

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var suppressDebugDetails = context.GetEndpoint()?.Metadata
                .GetMetadata<SuppressDebugExceptionDetailsAttribute>() is not null;
            var result = new AZOAResult<object>();
            if (suppressDebugDetails)
            {
                context.Response.Headers.CacheControl = "no-store";
                result.IsError = true;
                result.Message = "An unexpected error occurred.";
            }
            else
            {
                result.CaptureException(
                    ex,
                    AZOAResultDebug.Enabled
                        ? ex.Message
                        : "An unexpected error occurred. Enable debug mode (AZOA:DebugErrors) for details.");
            }

            var json = JsonSerializer.Serialize(result.ToErrorPayload(), JsonOptions);
            await context.Response.WriteAsync(json);
        }
    }
}
