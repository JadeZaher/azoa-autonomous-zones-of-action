using System.Collections.Concurrent;
using System.Text.Json;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Rolls up anonymous login/enrollment failures (no real challenge, enrollment or admin) instead of chaining each one (M-3).</summary>
public sealed class NodeAdminAnonymousFailures(ILogger<NodeAdminAnonymousFailures> logger)
{
    private ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <summary>Counts one failure and emits a structured log line; never touches the audit chain.</summary>
    public void Record(string action, string reason, NodeAdminRequestInfo info)
    {
        _counts.AddOrUpdate($"{action}:{reason}", 1, (_, count) => count + 1);
        logger.LogWarning("NodeAdminAnonymousFailure action={Action} reason={Reason} ip={Ip} requestId={RequestId}",
            action, reason, info.Ip, info.RequestId);
    }

    /// <summary>Puts drained counts back (a flush that could not be written).</summary>
    public void Restore(IReadOnlyDictionary<string, long> counts)
    {
        foreach (var (key, count) in counts)
            _counts.AddOrUpdate(key, count, (_, existing) => existing + count);
    }

    /// <summary>Takes the counts accumulated since the last drain.</summary>
    public IReadOnlyDictionary<string, long> Drain()
        => Interlocked.Exchange(ref _counts, new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
}

/// <summary>Flushes the rolled-up anonymous failures as one <c>auth.anonymous_failures</c> audit row per 5-minute window.</summary>
public sealed class NodeAdminAnonymousFailureFlusher(
    NodeAdminAnonymousFailures failures,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<NodeAdminAnonymousFailureFlusher> logger) : BackgroundService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Window, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await FlushAsync(CancellationToken.None);
        }
    }

    /// <summary>Writes one rolled-up row when anything was counted; a failed write puts the counts back for the next window.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var counts = failures.Drain();
        if (counts.Count == 0) return;
        var total = counts.Values.Sum();
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NodeAdminAuditLog>().AppendAsync(new NodeAdminAuditEntry(
                "anonymous", [], "auth.anonymous_failures", "node", NodeAdminAuditLog.OutcomeFailed,
                JsonSerializer.Serialize(new { count = total, reasons = counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary() }),
                string.Empty, string.Empty), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Rolled-up node-admin anonymous failures ({Count}) could not be audited; retrying next window.", total);
            failures.Restore(counts);
        }
    }
}
