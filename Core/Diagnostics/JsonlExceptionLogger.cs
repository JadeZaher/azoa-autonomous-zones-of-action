using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>
/// <see cref="ILogger"/> implementation that converts structured log calls into
/// <see cref="JsonlEntry"/> records and forwards them to <see cref="JsonlExceptionWriter"/>.
/// </summary>
public sealed class JsonlExceptionLogger : ILogger
{
    private readonly string _category;
    private readonly JsonlExceptionWriter _writer;
    private readonly JsonlExceptionLoggerOptions _options;
    private readonly IExternalScopeProvider _scopeProvider;

    public JsonlExceptionLogger(
        string category,
        JsonlExceptionWriter writer,
        JsonlExceptionLoggerOptions options,
        IExternalScopeProvider scopeProvider)
    {
        _category = category;
        _writer   = writer;
        _options  = options;
        _scopeProvider = scopeProvider;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => _scopeProvider.Push(state);

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel)
        => _options.Enabled && logLevel != LogLevel.None;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var scopeValues = ReadScopeValues();

        // Harvest SurrealDB context from Exception.Data.
        string? surrealStatement = null;
        Dictionary<string, object?>? surrealParams = null;

        if (exception is not null)
        {
            surrealStatement = exception.Data["SurrealStatement"] as string;
            if (exception.Data["SurrealParams"] is IEnumerable<KeyValuePair<string, object?>> rawParams)
            {
                surrealParams = RedactionFilter.Redact(rawParams, _options.RedactionKeys);
            }
        }

        // Build inner exception chain.
        List<string>? innerChain = null;
        var inner = exception?.InnerException;
        while (inner is not null)
        {
            (innerChain ??= []).Add(RedactionFilter.RedactJson(
                $"{inner.GetType().FullName}: {inner.Message}",
                _options.RedactionKeys));
            inner = inner.InnerException;
        }

        var entry = new JsonlEntry
        {
            Ts               = DateTime.UtcNow.ToString("O"),
            Level            = logLevel.ToString(),
            Category         = _category,
            EventId          = eventId.Id,
            Message          = RedactionFilter.RedactJson(message, _options.RedactionKeys),
            ExceptionType    = exception?.GetType().FullName,
            ExceptionMessage = exception is null
                ? null
                : RedactionFilter.RedactJson(exception.Message, _options.RedactionKeys),
            Stack            = exception?.StackTrace,
            InnerChain       = innerChain,
            RequestId        = ScopeString(scopeValues, "requestId"),
            RequestMethod    = ScopeString(scopeValues, "requestMethod"),
            RequestPath      = ScopeString(scopeValues, "requestPath"),
            StatusCode       = ScopeInt(scopeValues, "statusCode"),
            TraceId          = ScopeString(scopeValues, "TraceId"),
            SpanId           = ScopeString(scopeValues, "SpanId"),
            SurrealStatement = surrealStatement,
            SurrealParams    = surrealParams,
        };

        _writer.Enqueue(entry);
    }

    private Dictionary<string, object?> ReadScopeValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _scopeProvider.ForEachScope(static (scope, target) =>
        {
            if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs)
                return;

            foreach (var pair in pairs)
                target[pair.Key] = pair.Value;
        }, values);
        return values;
    }

    private static string? ScopeString(
        IReadOnlyDictionary<string, object?> values,
        string key)
        => values.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? ScopeInt(
        IReadOnlyDictionary<string, object?> values,
        string key)
        => values.TryGetValue(key, out var value)
            && int.TryParse(value?.ToString(), out var parsed)
                ? parsed
                : null;
}
