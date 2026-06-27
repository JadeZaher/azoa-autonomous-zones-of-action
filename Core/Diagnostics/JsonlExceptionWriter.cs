using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>
/// Singleton background worker that dequeues <see cref="JsonlEntry"/> items and
/// appends them to a rotating JSONL file (one file per UTC day).
/// Registered as <see cref="IHostedService"/> so the channel is drained on shutdown.
/// </summary>
public sealed class JsonlExceptionWriter : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JsonlExceptionLoggerOptions _options;
    private readonly Channel<JsonlEntry> _channel;
    private Task? _consumer;
    private CancellationTokenSource? _cts;

    public JsonlExceptionWriter(JsonlExceptionLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _channel = Channel.CreateBounded<JsonlEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Enqueues an entry for writing; never blocks (drop-oldest on overflow).</summary>
    public void Enqueue(JsonlEntry entry) => _channel.Writer.TryWrite(entry);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumer = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        _cts?.Cancel();
        if (_consumer is not null)
        {
            try { await _consumer.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try { await WriteEntryAsync(entry).ConfigureAwait(false); }
            catch { /* swallow individual write failures — never crash the host */ }
        }
    }

    private async Task WriteEntryAsync(JsonlEntry entry)
    {
        var dir = _options.Directory;
        if (!Path.IsPathRooted(dir))
        {
            // Resolve relative path from the current process directory.
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        }

        Directory.CreateDirectory(dir);

        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var file = Path.Combine(dir, $"{date}.jsonl");

        var line = JsonSerializer.Serialize(entry, JsonOptions);

        // Enforce max size guard.
        if (System.Text.Encoding.UTF8.GetByteCount(line) > _options.MaxEntrySizeBytes)
        {
            line = line.Substring(0, Math.Min(line.Length, _options.MaxEntrySizeBytes / 2)) + "...[truncated]\"}}";
        }

        // FileShare.ReadWrite allows external log tailing without locking.
        await using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var sw = new StreamWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        await sw.WriteLineAsync(line).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts?.Dispose();
    }
}
