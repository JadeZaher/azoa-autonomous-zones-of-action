using System.Text.Json;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Middleware;

/// <summary>
/// Converts an unhandled exception into a structured JSON response that mirrors
/// the <see cref="AZOAResult{T}"/> error shape, so the SDK can parse it like
/// any other error. When debug mode is on the response carries the full
/// exception chain; otherwise only a generic message is returned.
///
/// Without this, a thrown exception escaping a controller produced an empty
/// HTTP 500 with no body — the exact cause of the opaque
/// "GET /api/bridge/history failed with HTTP 500" with no further detail.
/// </summary>
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
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            // Headers already flushed — can't rewrite the response, let it bubble.
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var result = new AZOAResult<object>();
            result.CaptureException(
                ex,
                AZOAResultDebug.Enabled
                    ? ex.Message
                    : "An unexpected error occurred. Enable debug mode (AZOA:DebugErrors) for details.");

            var json = JsonSerializer.Serialize(result.ToErrorPayload(), JsonOptions);
            await context.Response.WriteAsync(json);
        }
    }
}
