using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed class SurrealAdminBootstrapStateStore : IAdminBootstrapStateStore
{
    private readonly ISurrealExecutor _executor;

    public SurrealAdminBootstrapStateStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<AdminBootstrapState?>> GetAsync(CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<AdminBootstrapState>(
            SurrealQuery<AdminBootstrapState>.Key(AdminBootstrapState.LocalId), ct);
        return AZOAResult<AdminBootstrapState?>.Success(row, row is null ? "Not bootstrapped." : "Success");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<AdminBootstrapState>> BindOnceAsync(
        AdminBootstrapState state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(state), ct);
            response.EnsureAllOk();
            var created = response.GetValues<AdminBootstrapState>(0).SingleOrDefault()
                ?? throw new InvalidOperationException("Bootstrap binding write returned no row.");
            return AZOAResult<AdminBootstrapState>.Success(created, "Bootstrap binding created.");
        }
        catch (SurrealStatementException)
        {
            var existing = await GetAsync(ct);
            if (existing.IsError || existing.Result is null)
                throw;

            return AZOAResult<AdminBootstrapState>.Success(existing.Result, "Bootstrap binding already exists.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<AdminBootstrapState>> RotateCredentialsAsync(
        Guid avatarId,
        string username,
        string passwordHash,
        long expectedRevision,
        long nextRevision,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        if (nextRevision <= expectedRevision)
            return AZOAResult<AdminBootstrapState>.Failure(
                "Node operator credential revision must increase.");

        var avatarRecordId = SurrealId.ToSurrealId(avatarId);
        var avatarLink = SurrealLink.ToLink("avatar", avatarRecordId);
        // raw: credential revision CAS, hash rotation, and JWT watermark advance must commit together.
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_binding = SELECT VALUE credential_revision FROM type::record($_state_table, $_state_id) WHERE avatar_id = $_avatar")
                .WithParam("_state_table", AdminBootstrapState.SchemaNameConst)
                .WithParam("_state_id", AdminBootstrapState.LocalId)
                .WithParam("_avatar", avatarLink),
            SurrealQuery
                .Of("IF array::len($_binding) != 1 OR $_binding[0] != $_expected { THROW 'Node operator credential revision conflict' }")
                .WithParam("_expected", expectedRevision),
            SurrealQuery
                .Of("LET $_avatar_updated = (UPDATE type::record($_avatar_table, $_avatar_id) SET username = $_username, password_hash = $_password_hash, auth_not_before = $_changed_at RETURN AFTER)")
                .WithParam("_avatar_table", "avatar")
                .WithParam("_avatar_id", avatarRecordId)
                .WithParam("_username", username)
                .WithParam("_password_hash", passwordHash)
                .WithParam("_changed_at", changedAt),
            SurrealQuery.Of("IF array::len($_avatar_updated) != 1 { THROW 'Node operator avatar is missing' }"),
            SurrealQuery
                .Of("LET $_state_updated = (UPDATE type::record($_state_table, $_state_id) SET credential_revision = $_next, credential_updated_at = $_changed_at RETURN AFTER)")
                .WithParam("_state_table", AdminBootstrapState.SchemaNameConst)
                .WithParam("_state_id", AdminBootstrapState.LocalId)
                .WithParam("_next", nextRevision)
                .WithParam("_changed_at", changedAt),
            SurrealQuery.Of("IF array::len($_state_updated) != 1 { THROW 'Node operator binding is missing' }"),
            SurrealQuery.Of("RETURN $_state_updated[0]"),
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var executed = await _executor.ExecuteAsync(transaction, ct);
                executed.EnsureAllOk();
                return executed;
            }, ct);
            var state = response.GetValues<AdminBootstrapState>(7).SingleOrDefault();
            return state is null
                ? AZOAResult<AdminBootstrapState>.Failure("Node operator credential rotation returned no state.")
                : AZOAResult<AdminBootstrapState>.Success(state, "Node operator credentials rotated.");
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("credential revision conflict", StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<AdminBootstrapState>.Failure("Node operator credential revision conflict.");
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<long>> AdvanceSessionWatermarkAsync(
        long expectedCredentialRevision,
        long expectedSessionRevision,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        var operatorId = AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.AvatarId;
        var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(operatorId));
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery
                .Of("LET $_binding = SELECT credential_revision, session_revision FROM type::record($_state_table, $_state_id) WHERE avatar_id = $_avatar")
                .WithParam("_state_table", AdminBootstrapState.SchemaNameConst)
                .WithParam("_state_id", AdminBootstrapState.LocalId)
                .WithParam("_avatar", avatarLink),
            SurrealQuery
                .Of("IF array::len($_binding) != 1 OR $_binding[0].credential_revision != $_expected_credential OR $_binding[0].session_revision != $_expected_session { THROW 'Node operator session revision conflict' }")
                .WithParam("_expected_credential", expectedCredentialRevision)
                .WithParam("_expected_session", expectedSessionRevision),
            SurrealQuery
                .Of("LET $_updated = (UPDATE type::record($_avatar_table, $_avatar_id) SET auth_not_before = $_changed_at WHERE email = $_reserved_email RETURN AFTER)")
                .WithParam("_avatar_table", "avatar")
                .WithParam("_avatar_id", SurrealId.ToSurrealId(operatorId))
                .WithParam("_changed_at", changedAt)
                .WithParam("_reserved_email", AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.ReservedEmail),
            SurrealQuery.Of("IF array::len($_updated) != 1 { THROW 'Node operator avatar integrity conflict' }"),
            SurrealQuery
                .Of("LET $_state_updated = (UPDATE type::record($_state_table, $_state_id) SET session_revision = $_next_session WHERE credential_revision = $_expected_credential AND session_revision = $_expected_session RETURN AFTER)")
                .WithParam("_state_table", AdminBootstrapState.SchemaNameConst)
                .WithParam("_state_id", AdminBootstrapState.LocalId)
                .WithParam("_next_session", expectedSessionRevision + 1)
                .WithParam("_expected_credential", expectedCredentialRevision)
                .WithParam("_expected_session", expectedSessionRevision),
            SurrealQuery.Of("IF array::len($_state_updated) != 1 { THROW 'Node operator session revision conflict' }"),
            SurrealQuery.Of("COMMIT"));

        try
        {
            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            return AZOAResult<long>.Success(expectedSessionRevision + 1, "Node operator sessions revoked.");
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("Node operator", StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<long>.Failure("Node operator session revocation conflict.");
        }
    }
}
