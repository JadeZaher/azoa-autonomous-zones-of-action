using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Seeds the directory, root-state watermark and built-in roles idempotently at boot; never refuses boot (M5).</summary>
public sealed class NodeAdminBootstrapService(
    IServiceScopeFactory scopes,
    NodeAdminRuntime runtime,
    ILogger<NodeAdminBootstrapService> logger) : IHostedService, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _retry;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Node-admin bootstrap failed; retrying in the background. Node-admin sign-in fails closed until it succeeds.");
            _retry = RetryAsync(_stopping.Token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_retry is not null)
            await Task.WhenAny(_retry, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryDelay, ct);
                await EnsureAsync(ct);
                logger.LogInformation("Node-admin bootstrap succeeded on retry.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Node-admin bootstrap retry failed.");
            }
        }
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SurrealNodeAdminStore>();
        var audit = scope.ServiceProvider.GetRequiredService<NodeAdminAuditLog>();

        if (await store.GetDirectoryAsync(ct) is null)
            await store.CommitAsync(null, [SurrealWriter.Create(new NodeAdminDirectory())], ct);
        await EnsureRootStateAsync(store, audit, ct);
        foreach (var builtIn in NodeAdminPermissions.BuiltInRoles)
            await EnsureRoleAsync(store, builtIn, ct);
    }

    private async Task EnsureRootStateAsync(SurrealNodeAdminStore store, NodeAdminAuditLog audit, CancellationToken ct)
    {
        var configured = Math.Max(0, runtime.RootKeyRevision);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var state = await store.GetRootStateAsync(ct);
            if (state is null)
            {
                if (await store.CommitAsync(null, [SurrealWriter.Create(new NodeAdminRootState { RootKeyRevision = configured, UpdatedAt = now })], ct)
                    == NodeAdminCommit.Committed)
                    return;
                continue;
            }
            if (configured == state.RootKeyRevision) return;
            if (configured < state.RootKeyRevision)
            {
                var reason = $"NodeAdmin:RootKeyRevision {configured} is lower than the durable root key revision {state.RootKeyRevision} (rollback refused).";
                runtime.DisableRootLogin(reason);
                logger.LogCritical("Node-admin root login is DISABLED: {Reason}", reason);
                return;
            }
            var advance = SurrealWriter.UpdateOnly<NodeAdminRootState>(NodeAdminRootState.SingletonId)
                .Where(row => row.RootKeyRevision == state.RootKeyRevision && row.RootSessionRevision == state.RootSessionRevision)
                .Set(row => row.RootKeyRevision, configured)
                .Set(row => row.RootSessionRevision, state.RootSessionRevision + 1)
                .Set(row => row.UpdatedAt, now)
                .Build();
            if (await store.CommitAsync(null, [advance], ct) != NodeAdminCommit.Committed) continue;
            logger.LogWarning("Node-admin root key revision advanced from {Previous} to {Current}; every root session ended.",
                state.RootKeyRevision, configured);
            await audit.AppendAsync(new NodeAdminAuditEntry("system", [], "root.revision_advanced", "root",
                NodeAdminAuditLog.OutcomeOk, JsonSerializer.Serialize(new { previous = state.RootKeyRevision, current = configured }),
                string.Empty, string.Empty), ct);
            return;
        }
        throw new InvalidOperationException("Node-admin root state could not be reconciled after repeated conflicts.");
    }

    private static async Task EnsureRoleAsync(SurrealNodeAdminStore store, NodeAdminBuiltInRole builtIn, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var directory = await store.GetDirectoryAsync(ct)
                ?? throw new InvalidOperationException("Node-admin directory is missing.");
            var existing = await store.GetRoleAsync(builtIn.Id, ct);
            var now = DateTimeOffset.UtcNow;
            SurrealQuery mutation;
            if (existing is null)
            {
                mutation = SurrealWriter.Create(new NodeAdminRole
                {
                    Id = builtIn.Id,
                    Name = builtIn.Name,
                    BuiltIn = true,
                    Permissions = builtIn.Permissions,
                    Revision = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else if (existing.BuiltIn && existing.Name == builtIn.Name && existing.Permissions.SequenceEqual(builtIn.Permissions, StringComparer.Ordinal))
            {
                return;
            }
            else
            {
                mutation = SurrealWriter.UpdateOnly<NodeAdminRole>(existing.Id)
                    .Where(row => row.Revision == existing.Revision)
                    .Set(row => row.Name, builtIn.Name)
                    .Set(row => row.BuiltIn, true)
                    .Set(row => row.Permissions, builtIn.Permissions)
                    .Set(row => row.Revision, existing.Revision + 1)
                    .Set(row => row.UpdatedAt, now)
                    .Build();
            }
            if (await store.CommitAsync(directory.Revision, [mutation], ct) == NodeAdminCommit.Committed) return;
        }
        throw new InvalidOperationException($"Built-in node-admin role '{builtIn.Id}' could not be seeded.");
    }
}
