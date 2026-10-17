using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// Captures the host's warning-and-above log lines so a failing flow assertion can show the
/// server-side cause next to the HTTP response (the end sweep diagnoses from output, not reruns).
/// </summary>
public sealed class ServerLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The most recent <paramref name="count"/> captured lines, oldest first.</summary>
    public string Tail(int count = 15) => string.Join(Environment.NewLine, _lines.ToArray().TakeLast(count));

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Sink(ServerLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cause = exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}";
            log._lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{cause}");
        }
    }
}
