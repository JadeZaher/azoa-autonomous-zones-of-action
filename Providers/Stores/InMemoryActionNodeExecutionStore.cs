using System.Collections.Concurrent;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores;

/// <summary>
/// Thread-safe in-memory <see cref="IActionNodeExecutionStore"/>.
/// Singleton-scoped via <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// The natural key <c>(RunId, NodeId)</c> is indexed via a secondary
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> for O(1) lookup;
/// <see cref="TryClaimPendingAsync"/> uses
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> to provide the
/// G2 conditional-update semantic (succeeds only when current state is
/// <see cref="ActionNodeState.Pending"/>).
///
/// <para>
/// HIGH#7 contract additions:
/// <list type="bullet">
///   <item>All read paths (<see cref="GetByIdAsync"/>,
///         <see cref="GetByRunIdAsync"/>,
///         <see cref="GetByRunAndNodeAsync"/>) return defensive
///         <see cref="ActionNodeExecution.Clone"/>s so callers cannot mutate
///         the store's internal state through a returned reference.</item>
///   <item><see cref="UpdateAsync"/> honours the optional
///         <see cref="ActionNodeState"/> guard — drift between the
///         expected pre-state and the stored value yields an error result
///         instead of an unconditional overwrite. Closes the
///         "ForkAsync vs in-flight-success" race identified in the swarm
///         review.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class InMemoryActionNodeExecutionStore : IActionNodeExecutionStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ActionNodeExecution?>> TryAdmitAsync(ActionNodeExecution execution, CancellationToken ct = default, long? expectedRunVersion = null)
    {
        if (execution.State != ActionNodeState.Pending || string.IsNullOrWhiteSpace(execution.FrozenInput))
            return AZOAResult<ActionNodeExecution?>.Failure("Frozen input and a Pending execution are required.");
        var candidate = execution.Clone();
        candidate.State = ActionNodeState.Running;
        candidate.StartedAt = DateTime.UtcNow;
        var claimed = await UpdateAsync(candidate, ActionNodeState.Pending, ct);
        return AZOAResult<ActionNodeExecution?>.Success(claimed.IsError ? null : claimed.Result);
    }

    // Keyed by execution row Id
    private readonly ConcurrentDictionary<Guid, ActionNodeExecution> _byId = new();

    // Secondary index: (RunId, NodeId) -> execution Id
    private readonly ConcurrentDictionary<(Guid RunId, Guid NodeId), Guid> _byNaturalKey = new();

    public Task<AZOAResult<ActionNodeExecution>> CreateAsync(ActionNodeExecution execution, CancellationToken ct = default)
    {
        // Store our own copy so a later caller-side mutation can't reach the
        // store's internal value. Symmetric with the defensive read-side
        // clones below.
        var stored = execution.Clone();

        if (!_byId.TryAdd(stored.Id, stored))
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution>
            {
                IsError = true,
                Message = $"ActionNodeExecution {execution.Id} already exists.",
                Result = null
            });
        }

        if (!_byNaturalKey.TryAdd((stored.RunId, stored.NodeId), stored.Id))
        {
            // Roll back the primary insert to keep both indexes consistent.
            _byId.TryRemove(stored.Id, out _);
            return Task.FromResult(new AZOAResult<ActionNodeExecution>
            {
                IsError = true,
                Message = $"ActionNodeExecution already exists for (run={execution.RunId}, node={execution.NodeId}).",
                Result = null
            });
        }

        return Task.FromResult(new AZOAResult<ActionNodeExecution> { Result = stored.Clone(), Message = "Created." });
    }

    public Task<AZOAResult<ActionNodeExecution>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var exec))
            return Task.FromResult(new AZOAResult<ActionNodeExecution> { Result = exec.Clone(), Message = "Success" });

        return Task.FromResult(new AZOAResult<ActionNodeExecution>
        {
            IsError = true,
            Message = $"ActionNodeExecution {id} not found.",
            Result = null
        });
    }

    public Task<AZOAResult<ActionNodeExecution>> UpdateAsync(
        ActionNodeExecution execution,
        ActionNodeState? expectedState = null,
        CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(execution.Id, out var current))
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution>
            {
                IsError = true,
                Message = $"ActionNodeExecution {execution.Id} not found.",
                Result = null
            });
        }

        // HIGH#7 — state-machine guard. When the caller asserts the prior
        // state, refuse the update if the store drifted underneath us
        // (e.g. ForkAsync cancelled the row Running → Cancelled while we
        // were busy producing a Succeeded transition). Returning an error
        // rather than overwriting prevents the "succeeded execution
        // silently turned into a skipped cancel" outcome from the swarm
        // race scenario.
        if (expectedState.HasValue && current.State != expectedState.Value)
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution>
            {
                IsError = true,
                Message =
                    $"state-machine guard rejected update; expected={expectedState.Value} actual={current.State}",
                Result = null
            });
        }

        var stored = execution.Clone();
        stored.Version = current.Version + 1;
        if (execution.Version != current.Version || !ActionOperationIdentity.CanUpdate(current, execution)
            || !_byId.TryUpdate(execution.Id, stored, current))
            return Task.FromResult(AZOAResult<ActionNodeExecution>.Failure("Node execution version/state changed."));
        execution.Version = stored.Version;
        return Task.FromResult(new AZOAResult<ActionNodeExecution> { Result = stored.Clone(), Message = "Updated." });
    }

    public Task<AZOAResult<IEnumerable<ActionNodeExecution>>> GetByRunIdAsync(Guid runId, CancellationToken ct = default)
    {
        // Hand each row out as a defensive clone — callers iterating the
        // returned sequence cannot mutate the store via the returned
        // execution objects (HIGH#7).
        IEnumerable<ActionNodeExecution> matches = _byId.Values
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.StartedAt)
            .Select(e => e.Clone())
            .ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionNodeExecution>> { Result = matches, Message = "Success" });
    }

    public Task<AZOAResult<ActionNodeExecution>> GetByRunAndNodeAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        if (_byNaturalKey.TryGetValue((runId, nodeId), out var execId) &&
            _byId.TryGetValue(execId, out var exec))
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution> { Result = exec.Clone(), Message = "Success" });
        }

        return Task.FromResult(new AZOAResult<ActionNodeExecution>
        {
            IsError = true,
            Message = $"No ActionNodeExecution for (run={runId}, node={nodeId}).",
            Result = null
        });
    }

    public Task<AZOAResult<ActionNodeExecution?>> TryClaimPendingAsync(Guid runId, Guid nodeId, CancellationToken ct = default)
    {
        if (!_byNaturalKey.TryGetValue((runId, nodeId), out var execId) ||
            !_byId.TryGetValue(execId, out var current))
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution?>
            {
                IsError = true,
                Message = $"No ActionNodeExecution for (run={runId}, node={nodeId}).",
                Result = null
            });
        }

        if (current.State != ActionNodeState.Pending)
        {
            // Row exists but not Pending — caller lost the race. Not an error.
            return Task.FromResult(new AZOAResult<ActionNodeExecution?>
            {
                Result = null,
                Message = $"ActionNodeExecution (run={runId}, node={nodeId}) is not Pending (current: {current.State})."
            });
        }

        var claimed = new ActionNodeExecution
        {
            Id = current.Id,
            RunId = current.RunId,
            NodeId = current.NodeId,
            State = ActionNodeState.Running,
            Output = current.Output,
            Error = current.Error,
            TxHash = current.TxHash,
            ChainType = current.ChainType,
            StartedAt = DateTime.UtcNow,
            EndedAt = current.EndedAt,
            Version = current.Version + 1,
            WaitUntil = current.WaitUntil,
            FrozenInput = current.FrozenInput,
            ExternalOperation = current.ExternalOperation,
            RecoveryState = current.RecoveryState,
        };

        // Conditional CAS: only swap if the row we read is still the current row.
        // Mirrors the SurrealDB UPDATE … WHERE state='Pending' RETURN AFTER semantic.
        if (_byId.TryUpdate(current.Id, claimed, current))
        {
            return Task.FromResult(new AZOAResult<ActionNodeExecution?>
            {
                Result = claimed.Clone(),
                Message = "Claimed."
            });
        }

        // Lost the race — another caller updated this row between our read and our CAS.
        return Task.FromResult(new AZOAResult<ActionNodeExecution?>
        {
            Result = null,
            Message = $"ActionNodeExecution (run={runId}, node={nodeId}) was concurrently modified."
        });
    }
}
