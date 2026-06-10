using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace OASIS.WebAPI.Core.Diagnostics;

/// <summary>
/// <see cref="ILogger"/> implementation that converts structured log calls into
/// <see cref="JsonlEntry"/> records and forwards them to <see cref="JsonlExceptionWriter"/>.
/// </summary>
public sealed class JsonlExceptionLogger : ILogger
{
    private readonly string _category;
    private readonly JsonlExceptionWriter _writer;
    private readonly JsonlExceptionLoggerOptions _options;

    public JsonlExceptionLogger(
        string category,
        JsonlExceptionWriter writer,
        JsonlExceptionLoggerOptions options)
    {
        _category = category;
        _writer   = writer;
        _options  = options;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel)
        => _options.Enabled && logLevel >= _options.MinimumLevel && logLevel != LogLevel.None;

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
            (innerChain ??= []).Add($"{inner.GetType().FullName}: {inner.Message}");
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
            ExceptionMessage = exception?.Message,
            Stack            = exception?.StackTrace,
            InnerChain       = innerChain,
            SurrealStatement = surrealStatement,
            SurrealParams    = surrealParams,
        };

        _writer.Enqueue(entry);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
