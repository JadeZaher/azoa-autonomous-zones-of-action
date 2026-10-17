using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using ForkEdge = AZOA.WebAPI.Persistence.SurrealDb.Models.ForkedFrom;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealActionRunStore(ISurrealExecutor executor) : IActionRunStore
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> CreateAsync(ActionRun run, CancellationToken ct = default)
    {
        if (run is null || run.Id == Guid.Empty) return AZOAResult<ActionRun>.Failure("Run identity is required.");
        var create = SurrealWriter.Create(ToRow(run));
        if (run.ParentRunId is { } parent)
        {
            // raw: fork row and lineage edge must commit together; see AGENTS.md.
            create = SurrealQuery.Combine(SurrealQuery.Of("BEGIN"),
                SurrealGeneratedStatementScope.Apply(create, "run"),
                SurrealGeneratedStatementScope.Apply(SurrealQuery.Relate(
                    SurrealRecordId.Create(Row.SchemaNameConst, run.Id.ToString("N")), ForkEdge.SchemaNameConst,
                    SurrealRecordId.Create(Row.SchemaNameConst, parent.ToString("N"))).WithContent(new { }), "edge"),
                SurrealQuery.Of("COMMIT"));
        }
        try
        {
            var response = await executor.ExecuteAsync(create, ct);
            response.EnsureAllOk();
            return AZOAResult<ActionRun>.Success(run);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return AZOAResult<ActionRun>.Failure("Action run already exists.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(id.ToString("N")), ct);
        return row is null ? AZOAResult<ActionRun>.Failure("Action run not found.") : AZOAResult<ActionRun>.Success(ToDomain(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionRun>> UpdateAsync(ActionRun run, ActionRunStatus? expectedStatus = null, CancellationToken ct = default)
    {
        var update = SurrealWriter.UpdateOnly<Row>(run.Id.ToString("N"))
            .Where(row => row.Version == run.Version).Set(row => row.Version, run.Version + 1)
            .Set(row => row.Status, (Row.ActionRunStatusKind)(int)run.Status);
        if (expectedStatus is { } expected)
        {
            var status = (Row.ActionRunStatusKind)(int)expected;
            update = update.Where(row => row.Status == status);
        }
        update = run.EndedAt is null ? update.Unset(row => row.EndedAt)
            : update.Set(row => row.EndedAt, new DateTimeOffset(DateTime.SpecifyKind(run.EndedAt.Value, DateTimeKind.Utc)));
        update = run.FailReason is null ? update.Unset(row => row.FailReason) : update.Set(row => row.FailReason, run.FailReason);
        var response = await executor.ExecuteAsync(update.Build(), ct);
        response.EnsureAllOk();
        var saved = response.GetValues<Row>(0).SingleOrDefault();
        if (saved is null) return AZOAResult<ActionRun>.Failure("Action run version/status changed.");
        run.Version = saved.Version;
        return AZOAResult<ActionRun>.Success(ToDomain(saved));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByActionIdAsync(Guid actionId, CancellationToken ct = default)
    {
        var id = SurrealLink.ToLink("action", actionId.ToString("N"));
        var rows = await executor.QueryAsync<Row>(SurrealQuery<Row>.From().Where(row => row.ActionId == id).OrderBy(row => row.StartedAt), ct);
        return AZOAResult<IEnumerable<ActionRun>>.Success(rows.Select(ToDomain).ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var id = SurrealLink.ToLink("user", userId.ToString("N"));
        var rows = await executor.QueryAsync<Row>(SurrealQuery<Row>.From().Where(row => row.UserId == id).OrderBy(row => row.StartedAt), ct);
        return AZOAResult<IEnumerable<ActionRun>>.Success(rows.Select(ToDomain).ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetByStatusAsync(ActionRunStatus status, CancellationToken ct = default)
    {
        var stored = (Row.ActionRunStatusKind)(int)status;
        var rows = await executor.QueryAsync<Row>(SurrealQuery<Row>.From().Where(row => row.Status == stored).OrderBy(row => row.StartedAt), ct);
        return AZOAResult<IEnumerable<ActionRun>>.Success(rows.Select(ToDomain).ToArray());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<ActionRun>>> GetLineageAsync(Guid runId, CancellationToken ct = default)
    {
        var seed = await GetByIdAsync(runId, ct);
        if (seed.IsError || seed.Result is null) return AZOAResult<IEnumerable<ActionRun>>.Failure(seed.Message);
        var rows = new List<ActionRun>(); var visited = new HashSet<Guid>(); var current = seed.Result;
        while (visited.Add(current.Id))
        {
            rows.Add(current);
            if (current.ParentRunId is null) break;
            var parents = await executor.QueryAsync<Row>(SurrealQuery<Row>.Key(current.Id.ToString("N"))
                .Traverse<Row>(row => row.Out<ForkEdge>().To<Row>()), ct);
            if (parents.Count == 0) break;
            current = ToDomain(parents.Single());
        }
        return AZOAResult<IEnumerable<ActionRun>>.Success(rows);
    }

    internal static Row ToRow(ActionRun row) => new()
    {
        Id = row.Id.ToString("N"), ActionId = SurrealLink.ToLink("action", row.ActionId.ToString("N"))!, UserId = SurrealLink.ToLink("user", row.UserId.ToString("N"))!,
        ActingTenantId = SurrealLink.ToLink("user", row.ActingTenantId?.ToString("N")), Status = (Row.ActionRunStatusKind)(int)row.Status,
        Version = row.Version, StartedAt = new DateTimeOffset(DateTime.SpecifyKind(row.StartedAt, DateTimeKind.Utc)),
        EndedAt = row.EndedAt is { } ended ? new DateTimeOffset(DateTime.SpecifyKind(ended, DateTimeKind.Utc)) : null,
        ParentRunId = SurrealLink.ToLink("action_run", row.ParentRunId?.ToString("N")), ForkedAtNodeId = SurrealLink.ToLink("action_node", row.ForkedAtNodeId?.ToString("N")),
        ForkReason = row.ForkReason, FailReason = row.FailReason, SourceActionId = SurrealLink.ToLink("action", row.SourceActionId?.ToString("N")),
        OriginUserId = SurrealLink.ToLink("user", row.OriginUserId?.ToString("N")), PublishedVersionHash = row.PublishedVersionHash,
        DefinitionSnapshotJson = row.DefinitionSnapshotJson, DefinitionSnapshotHash = row.DefinitionSnapshotHash,
        InvocationIdentity = row.InvocationIdentity, InternalDependenciesJson = row.InternalDependenciesJson, OccurrenceBoundariesJson = row.OccurrenceBoundariesJson,
        InitialInputJson = row.InitialInputJson, AuthenticationWitnessJson = row.AuthenticationWitnessJson,
    };

    internal static ActionRun ToDomain(Row row) => new()
    {
        Id = SurrealRecordGuid.Parse(row.Id), ActionId = SurrealRecordGuid.Parse(row.ActionId), UserId = SurrealRecordGuid.Parse(row.UserId),
        ActingTenantId = SurrealRecordGuid.ParseOptional(row.ActingTenantId), Status = (ActionRunStatus)(int)row.Status,
        Version = row.Version, StartedAt = row.StartedAt.UtcDateTime, EndedAt = row.EndedAt?.UtcDateTime,
        ParentRunId = SurrealRecordGuid.ParseOptional(row.ParentRunId), ForkedAtNodeId = SurrealRecordGuid.ParseOptional(row.ForkedAtNodeId),
        ForkReason = row.ForkReason, FailReason = row.FailReason, SourceActionId = SurrealRecordGuid.ParseOptional(row.SourceActionId),
        OriginUserId = SurrealRecordGuid.ParseOptional(row.OriginUserId), PublishedVersionHash = row.PublishedVersionHash,
        DefinitionSnapshotJson = row.DefinitionSnapshotJson, DefinitionSnapshotHash = row.DefinitionSnapshotHash,
        InvocationIdentity = row.InvocationIdentity, InternalDependenciesJson = row.InternalDependenciesJson, OccurrenceBoundariesJson = row.OccurrenceBoundariesJson,
        InitialInputJson = row.InitialInputJson, AuthenticationWitnessJson = row.AuthenticationWitnessJson,
    };
}
