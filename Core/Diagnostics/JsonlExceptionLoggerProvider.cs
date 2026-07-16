using System;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>
/// <see cref="ILoggerProvider"/> that returns a shared <see cref="JsonlExceptionLogger"/>
/// per category name. The underlying <see cref="JsonlExceptionWriter"/> is injected via
/// constructor and must be registered as a singleton before this provider is added.
/// </summary>
public sealed class JsonlExceptionLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly JsonlExceptionWriter _writer;
    private readonly JsonlExceptionLoggerOptions _options;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public JsonlExceptionLoggerProvider(
        JsonlExceptionWriter writer,
        JsonlExceptionLoggerOptions options)
    {
        _writer  = writer  ?? throw new ArgumentNullException(nameof(writer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
        => new JsonlExceptionLogger(categoryName, _writer, _options, _scopeProvider);

    /// <inheritdoc/>
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        => _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    /// <inheritdoc/>
    public void Dispose() { /* writer lifetime managed by IHostedService */ }
}
