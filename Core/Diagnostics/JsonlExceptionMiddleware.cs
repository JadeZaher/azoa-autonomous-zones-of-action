using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>
/// Pipeline observer that writes a JSONL row for any 4xx/5xx response or unhandled exception.
/// Observer-only — never alters the response. Re-throws exceptions so
/// <see cref="DebugExceptionMiddleware"/> continues to own response shaping.
/// Must be placed AFTER <c>UseRouting</c> and BEFORE <c>UseAuthentication</c>
/// so that 401/429 status codes set downstream are still captured.
/// </summary>
public sealed class JsonlExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JsonlExceptionMiddleware> _logger;

    public JsonlExceptionMiddleware(RequestDelegate next, ILogger<JsonlExceptionMiddleware> logger)
    {
        _next   = next   ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);

            // Capture non-exception pipeline failures (401, 429, 4xx/5xx set by downstream middleware).
            if (context.Response.StatusCode >= 400)
            {
                _logger.LogWarning(
                    "Pipeline response {StatusCode} for {Method} {Path} (requestId={RequestId})",
                    context.Response.StatusCode,
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier);
            }
        }
        catch (Exception ex)
        {
            // Harvest SurrealDB context to populate the log entry.
            var surrealStatement = ex.Data["SurrealStatement"] as string;
            object? rawParams    = ex.Data["SurrealParams"];

            // Build a structured scope so the ILogger (JsonlExceptionLogger) can pick up
            // request context on its Log<TState> call.
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["requestId"]     = context.TraceIdentifier,
                ["requestMethod"] = context.Request.Method,
                ["requestPath"]   = context.Request.Path.Value,
                ["statusCode"]    = context.Response.HasStarted ? context.Response.StatusCode : 500,
            }))
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception {ExceptionType} for {Method} {Path} " +
                    "(requestId={RequestId}, surrealStatement={SurrealStatement})",
                    ex.GetType().Name,
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier,
                    surrealStatement ?? "(none)");
            }

            // Re-throw — DebugExceptionMiddleware owns response shaping.
            throw;
        }
    }
}
