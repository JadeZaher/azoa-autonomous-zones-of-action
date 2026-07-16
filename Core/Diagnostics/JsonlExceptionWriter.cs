using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>Drains diagnostic entries to daily JSONL files.</summary>
public sealed class JsonlExceptionWriter : IHostedService, IDisposable
{
    private const int QueueCapacity = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JsonlExceptionLoggerOptions _options;
    private readonly Channel<JsonlEntry> _channel;
    private Task? _consumer;
    private CancellationTokenSource? _cts;
    private long _failureCount;

    public JsonlExceptionWriter(JsonlExceptionLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = Channel.CreateBounded<JsonlEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Number of queue, write, or forced-drain failures reported to the fallback sink.</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>Accepts an entry only when the bounded queue can preserve it.</summary>
    public bool Enqueue(JsonlEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_channel.Writer.TryWrite(entry))
            return true;

        ReportFailure("enqueue-rejected", null);
        return false;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_consumer is null)
            return;

        try
        {
            await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReportFailure("shutdown-drain", null);
            _cts?.Cancel();
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await WriteEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportFailure("write", ex);
            }
        }
    }

    private async Task WriteEntryAsync(JsonlEntry entry)
    {
        var dir = _options.Directory;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);

        Directory.CreateDirectory(dir);

        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var file = Path.Combine(dir, $"{date}.jsonl");
        var line = SerializeWithinLimit(entry, _options.MaxEntrySizeBytes);

        await using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        await sw.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static string SerializeWithinLimit(JsonlEntry entry, int configuredLimit)
    {
        var limit = Math.Max(2, configuredLimit);
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(line) <= limit)
            return line;

        var summary = entry with
        {
            Category = Truncate(entry.Category, 128)!,
            Message = "[entry truncated]",
            ExceptionMessage = null,
            Stack = null,
            InnerChain = null,
            RequestId = Truncate(entry.RequestId, 64),
            RequestMethod = Truncate(entry.RequestMethod, 16),
            RequestPath = Truncate(entry.RequestPath, 128),
            TraceId = Truncate(entry.TraceId, 64),
            SpanId = Truncate(entry.SpanId, 32),
            SurrealStatement = null,
            SurrealParams = null,
        };
        line = JsonSerializer.Serialize(summary, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(line) <= limit)
            return line;

        line = JsonSerializer.Serialize(new { message = "[entry truncated]" }, JsonOptions);
        return System.Text.Encoding.UTF8.GetByteCount(line) <= limit ? line : "{}";
    }

    private static string? Truncate(string? value, int maxLength)
        => value is not null && value.Length > maxLength
            ? value[..maxLength]
            : value;

    private void ReportFailure(string stage, Exception? exception)
    {
        var count = Interlocked.Increment(ref _failureCount);
        try
        {
            Console.Error.WriteLine(
                $"AZOA JSONL diagnostics {stage} failure " +
                $"({exception?.GetType().Name ?? "no exception detail"}); count={count}.");
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
