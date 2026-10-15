using System.Collections.Concurrent;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores;

/// <summary>
/// Thread-safe in-memory <see cref="IActionStore"/>. Singleton-scoped via
/// <c>Program.cs</c>. Used as the default during the
/// <c>action-temporal-fork-model</c> transition window; a SurrealDB-backed
/// implementation arrives with that track's runtime store landing
/// ([`surrealdb-migration` plan.md tasks 9–11]).
///
/// <para><b>Boundary with <see cref="IActionTemplateStore"/>.</b> The newer
/// definition-side <see cref="IActionTemplateStore"/> (Stream C2) handles
/// READ-ONLY template lookups consumed by <c>ActionInstantiator</c>. This
/// <see cref="IActionStore"/> is the legacy unified surface that
/// <see cref="AZOA.WebAPI.Managers.ActionManager"/> still consumes for
/// instantiated <see cref="ActionDefinition"/> CRUD plus full template / node-template
/// authoring. Once <c>action-temporal-fork-model</c> ships its Surreal store +
/// ActionManager refactor onto the per-aggregate seams, this in-memory adapter
/// can be deleted.</para>
///
/// <para><b>Data persistence caveat.</b> State is process-lifetime only. Loss
/// across restarts is acceptable for the transition window because
/// definition-side data is small + authored from configuration, and
/// instantiated actions are not yet user-visible. DO NOT keep this past the
/// fork-model landing.</para>
/// </summary>
public sealed class InMemoryActionStore : IActionStore
{
    private readonly ConcurrentDictionary<Guid, ActionDefinition> _actions = new();
    private readonly ConcurrentDictionary<Guid, ActionTemplate> _templates = new();
    private readonly ConcurrentDictionary<Guid, ActionNodeTemplate> _nodeTemplates = new();

    // ── Action CRUD ──────────────────────────────────────────────────────────────────────

    public Task<AZOAResult<ActionDefinition>> GetActionAsync(Guid id, CancellationToken ct = default)
    {
        if (_actions.TryGetValue(id, out var action))
            return Task.FromResult(new AZOAResult<ActionDefinition> { Result = action, Message = "Success" });

        return Task.FromResult(new AZOAResult<ActionDefinition>
        {
            IsError = true,
            Message = $"Action {id} not found.",
            Result = null,
        });
    }

    public Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        var matches = _actions.Values.Where(q => q.UserId == userId).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionDefinition>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<AZOAResult<IEnumerable<ActionDefinition>>> GetPublicActionsAsync(CancellationToken ct = default)
    {
        var matches = _actions.Values.Where(q => q.IsPublic).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionDefinition>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<AZOAResult<IEnumerable<ActionDefinition>>> GetActionsByDappSeriesAsync(
        Guid dappSeriesId, CancellationToken ct = default)
    {
        var matches = _actions.Values
            .Where(q => q.DappSeriesId.HasValue && q.DappSeriesId.Value == dappSeriesId)
            .ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionDefinition>>
        {
            Result = matches,
            Message = "Success",
        });
    }

    public Task<AZOAResult<ActionDefinition>> UpsertActionAsync(ActionDefinition action, CancellationToken ct = default)
    {
        _actions[action.Id] = action;
        return Task.FromResult(new AZOAResult<ActionDefinition> { Result = action, Message = "Upserted." });
    }

    public Task<AZOAResult<bool>> DeleteActionAsync(Guid id, CancellationToken ct = default)
    {
        var removed = _actions.TryRemove(id, out _);
        return Task.FromResult(new AZOAResult<bool>
        {
            Result = removed,
            Message = removed ? "Deleted." : $"Action {id} not found.",
            IsError = !removed,
        });
    }

    // ── F6 TOCTOU compare-and-swap (definition-status version guard) ──────────
    // In-memory CAS mirroring the SurrealActionStore conditional-UPDATE semantics:
    // returns the affected-row count VERBATIM (1 = won, 0 = lost the race / stale
    // version). This shim is inert in production (SurrealActionStore is the active
    // binding); the faithful CAS keeps it a correct drop-in per the interface.
    public Task<int> TryTransitionActionStatusAsync(
        Guid id, ActionStatus expected, ActionStatus next, long expectedVersion, CancellationToken ct = default)
    {
        if (_actions.TryGetValue(id, out var q) && q.Status == expected && q.Version == expectedVersion)
        {
            q.Status = next;
            q.Version = expectedVersion + 1;
            return Task.FromResult(1);
        }
        return Task.FromResult(0);
    }

    public Task<int> TryConfirmActionStateAsync(
        Guid id, ActionStatus expected, long expectedVersion, CancellationToken ct = default)
    {
        var ok = _actions.TryGetValue(id, out var q)
                 && q!.Status == expected
                 && q.Version == expectedVersion;
        return Task.FromResult(ok ? 1 : 0);
    }

    // ── ActionTemplate CRUD ────────────────────────────────────────────────────

    public Task<AZOAResult<ActionTemplate>> GetActionTemplateAsync(Guid id, CancellationToken ct = default)
    {
        if (_templates.TryGetValue(id, out var template))
            return Task.FromResult(new AZOAResult<ActionTemplate> { Result = template, Message = "Success" });

        return Task.FromResult(new AZOAResult<ActionTemplate>
        {
            IsError = true,
            Message = $"ActionTemplate {id} not found.",
            Result = null,
        });
    }

    public Task<AZOAResult<IEnumerable<ActionTemplate>>> GetAllActionTemplatesAsync(
        CancellationToken ct = default)
    {
        var snapshot = _templates.Values.ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionTemplate>>
        {
            Result = snapshot,
            Message = "Success",
        });
    }

    public Task<AZOAResult<ActionTemplate>> UpsertActionTemplateAsync(
        ActionTemplate template, CancellationToken ct = default)
    {
        _templates[template.Id] = template;
        return Task.FromResult(new AZOAResult<ActionTemplate> { Result = template, Message = "Upserted." });
    }

    public Task<AZOAResult<bool>> DeleteActionTemplateAsync(Guid id, CancellationToken ct = default)
    {
        var removed = _templates.TryRemove(id, out _);
        return Task.FromResult(new AZOAResult<bool>
        {
            Result = removed,
            Message = removed ? "Deleted." : $"ActionTemplate {id} not found.",
            IsError = !removed,
        });
    }

    // ── ActionNodeTemplate CRUD ────────────────────────────────────────────────

    public Task<AZOAResult<ActionNodeTemplate>> UpsertActionNodeTemplateAsync(
        ActionNodeTemplate template, CancellationToken ct = default)
    {
        _nodeTemplates[template.Id] = template;
        return Task.FromResult(new AZOAResult<ActionNodeTemplate>
        {
            Result = template,
            Message = "Upserted.",
        });
    }

    public Task<AZOAResult<IEnumerable<ActionNodeTemplate>>> GetAllActionNodeTemplatesAsync(
        CancellationToken ct = default)
    {
        var snapshot = _nodeTemplates.Values.ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionNodeTemplate>>
        {
            Result = snapshot,
            Message = "Success",
        });
    }
}
