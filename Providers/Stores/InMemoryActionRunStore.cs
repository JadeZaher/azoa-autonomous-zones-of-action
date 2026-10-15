using System.Collections.Concurrent;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores;

/// <summary>
/// Thread-safe in-memory <see cref="IActionRunStore"/>. Singleton-scoped via
/// <c>Program.cs</c>. Used as the default during the
/// action-temporal-fork-model + surrealdb-migration transition window;
/// SurrealDB-backed implementation arrives with surrealdb-migration tasks 9–10.
/// </summary>
public sealed class InMemoryActionRunStore : IActionRunStore
{
    private readonly ConcurrentDictionary<Guid, ActionRun> _runs = new();

    public Task<AZOAResult<ActionRun>> CreateAsync(ActionRun run, CancellationToken ct = default)
    {
        if (!_runs.TryAdd(run.Id, run))
        {
            return Task.FromResult(new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"ActionRun {run.Id} already exists.",
                Result = null
            });
        }
        return Task.FromResult(new AZOAResult<ActionRun> { Result = run, Message = "Created." });
    }

    public Task<AZOAResult<ActionRun>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (_runs.TryGetValue(id, out var run))
            return Task.FromResult(new AZOAResult<ActionRun> { Result = run, Message = "Success" });

        return Task.FromResult(new AZOAResult<ActionRun>
        {
            IsError = true,
            Message = $"ActionRun {id} not found.",
            Result = null
        });
    }

    public Task<AZOAResult<ActionRun>> UpdateAsync(
        ActionRun run, ActionRunStatus? expectedStatus = null, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(run.Id, out var current))
        {
            return Task.FromResult(new AZOAResult<ActionRun>
            {
                IsError = true,
                Message = $"ActionRun {run.Id} not found.",
                Result = null
            });
        }

        if (expectedStatus is { } expected)
        {
            // G2 single-winner conditional update: apply only if the stored
            // status still matches. Compare-and-swap on the dictionary entry so
            // two concurrent projectors don't clobber each other.
            if (current.Status != expected ||
                !_runs.TryUpdate(run.Id, run, current))
            {
                return Task.FromResult(new AZOAResult<ActionRun>
                {
                    IsError = true,
                    Message = $"ActionRun {run.Id} conditional update lost: status is no longer {expected}.",
                    Result = null
                });
            }
            return Task.FromResult(new AZOAResult<ActionRun> { Result = run, Message = "Updated." });
        }

        _runs[run.Id] = run;
        return Task.FromResult(new AZOAResult<ActionRun> { Result = run, Message = "Updated." });
    }

    public Task<AZOAResult<IEnumerable<ActionRun>>> GetByActionIdAsync(Guid actionId, CancellationToken ct = default)
    {
        IEnumerable<ActionRun> matches = _runs.Values.Where(r => r.ActionId == actionId).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionRun>> { Result = matches, Message = "Success" });
    }

    public Task<AZOAResult<IEnumerable<ActionRun>>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        IEnumerable<ActionRun> matches = _runs.Values.Where(r => r.UserId == userId).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionRun>> { Result = matches, Message = "Success" });
    }

    public Task<AZOAResult<IEnumerable<ActionRun>>> GetByStatusAsync(ActionRunStatus status, CancellationToken ct = default)
    {
        IEnumerable<ActionRun> matches = _runs.Values.Where(r => r.Status == status).ToList();
        return Task.FromResult(new AZOAResult<IEnumerable<ActionRun>> { Result = matches, Message = "Success" });
    }

    public Task<AZOAResult<IEnumerable<ActionRun>>> GetLineageAsync(Guid runId, CancellationToken ct = default)
    {
        if (!_runs.TryGetValue(runId, out var current))
        {
            return Task.FromResult(new AZOAResult<IEnumerable<ActionRun>>
            {
                IsError = true,
                Message = $"ActionRun {runId} not found.",
                Result = Array.Empty<ActionRun>()
            });
        }

        var chain = new List<ActionRun>();
        var visited = new HashSet<Guid>(); // structural safety against malformed data
        var cursor = (ActionRun?)current;
        while (cursor is not null && visited.Add(cursor.Id))
        {
            chain.Add(cursor);
            if (cursor.ParentRunId is null) break;
            _runs.TryGetValue(cursor.ParentRunId.Value, out cursor);
        }

        return Task.FromResult(new AZOAResult<IEnumerable<ActionRun>>
        {
            Result = chain,
            Message = "Success"
        });
    }
}
