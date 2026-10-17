using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealActionLocalMutationStore(ISurrealExecutor executor) : IActionLocalMutationStore
{
    private const string Conflict = "ACTION_LOCAL_WITNESS_CHANGED";

    /// <inheritdoc/>
    public Task<ActionLocalReceipt?> GetReceiptAsync(string commandId, CancellationToken ct = default)
        => executor.QuerySingleAsync<ActionLocalReceipt>(SurrealQuery<ActionLocalReceipt>.Key(commandId), ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<ActionLocalReceipt>> CommitAsync(ActionLocalMutationPlan plan, ActionLocalReceipt receipt,
        long? expectedPolicyVersion, bool requiresWalletScope, CancellationToken ct = default)
    {
        var previous = await GetReceiptAsync(receipt.Id, ct);
        if (previous is not null) return Replay(previous, receipt);
        if (!plan.IsWithinBounds()) return AZOAResult<ActionLocalReceipt>.Failure(plan.Refusal!);
        if (receipt.IsError && (plan.HolonWrites.Count != 0 || plan.WalletWrites.Count != 0))
            return AZOAResult<ActionLocalReceipt>.Failure("A rejected local command cannot contain effects.");
        if (!Guid.TryParseExact(receipt.ActorId, "N", out var actor)
            || plan.HolonWrites.Keys.Any(id => !plan.HolonReads.ContainsKey(id))
            || plan.WalletWrites.Keys.Any(id => !plan.WalletReads.ContainsKey(id))
            || plan.HolonReads.Values.Concat(plan.HolonWrites.Values).Any(row => row?.AssetType == ConsumerGovernanceHolonTypes.ProposalAssetType)
            || plan.HolonWrites.Any(pair => (pair.Value?.UserId ?? plan.HolonReads.GetValueOrDefault(pair.Key)?.UserId) != actor)
            || plan.WalletWrites.Any(pair => (pair.Value?.UserId ?? plan.WalletReads.GetValueOrDefault(pair.Key)?.UserId) != actor))
            return AZOAResult<ActionLocalReceipt>.Failure("Local action authority does not permit this mutation plan.");

        // raw: action authority, policy, graph witnesses, all local effects and receipt share one transaction.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(ActionExecutionFence.Create(receipt.RunId, receipt.NodeId, receipt.ActorId, receipt.TenantId, receipt.IntentHash));
        if (!receipt.IsError) statements.AddRange(NodeGovernancePolicyFence.Create(expectedPolicyVersion));
        statements.Add(LocalEntityMutationFence.Holons());
        statements.Add(LocalEntityMutationFence.Wallets());
        if (!receipt.IsError) statements.AddRange(Authority(receipt, requiresWalletScope));
        AppendWitnesses(statements, plan);
        var index = 0;
        foreach (var (id, row) in plan.HolonWrites.OrderBy(pair => pair.Key))
        {
            SurrealQuery write = row is null ? SurrealWriter.DeleteOnly<SurrealHolonStore.HolonPoco>(id.ToString("N")).Build()
                : plan.HolonReads.GetValueOrDefault(id) is null ? SurrealWriter.Create(SurrealHolonStore.ToPoco(row))
                : SurrealWriter.Upsert(SurrealHolonStore.ToPoco(row));
            statements.Add(SurrealGeneratedStatementScope.Apply(write, Name("holon_write", index++)));
            if (row is not null) AppendHolonNulls(statements, row, index++);
        }
        foreach (var (id, row) in plan.WalletWrites.OrderBy(pair => pair.Key))
        {
            SurrealQuery write = row is null ? SurrealWriter.DeleteOnly<Wallet>(id.ToString("N")).Build()
                : plan.WalletReads.GetValueOrDefault(id) is null ? SurrealWriter.Create(SurrealWalletStore.ToPoco(row))
                : SurrealWriter.Upsert(SurrealWalletStore.ToPoco(row));
            statements.Add(SurrealGeneratedStatementScope.Apply(write, Name("wallet_write", index++)));
            if (row is not null) AppendWalletNulls(statements, row, index++);
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "local_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(SurrealQuery.Combine(statements.ToArray()), ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return AZOAResult<ActionLocalReceipt>.Success(receipt);
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(NodeGovernancePolicyFence.FailureMarker, StringComparison.Ordinal))
        {
            var committed = await GetReceiptAsync(receipt.Id, ct);
            return committed is not null ? Replay(committed, receipt)
                : AZOAResult<ActionLocalReceipt>.Failure("Local action state or authority changed; the effect was not committed.");
        }
    }

    public static bool Same(ActionLocalReceipt saved, ActionLocalReceipt expected) => saved.Id == expected.Id
        && saved.RunId == expected.RunId && saved.NodeId == expected.NodeId && saved.ActorId == expected.ActorId
        && saved.TenantId == expected.TenantId && saved.IntentHash == expected.IntentHash;

    private static AZOAResult<ActionLocalReceipt> Replay(ActionLocalReceipt saved, ActionLocalReceipt expected)
        => Same(saved, expected) ? AZOAResult<ActionLocalReceipt>.Success(saved)
            : AZOAResult<ActionLocalReceipt>.Failure("Local command identity conflicts with its committed intent.");

    private static string Name(string prefix, int index) => prefix + "_" + string.Concat(BitConverter.GetBytes(index)
        .Select(value => $"{(char)('a' + value / 16)}{(char)('a' + value % 16)}"));

    // raw: current actor and delegated consent are fenced inside the local mutation commit.
    private static IReadOnlyList<SurrealQuery> Authority(ActionLocalReceipt receipt, bool wallet) =>
    [
        SurrealQuery.Of("LET $_local_actor = (UPDATE ONLY type::record('user', $_local_actor_id) SET is_active = is_active WHERE is_active = true RETURN AFTER)")
            .WithParam("_local_actor_id", receipt.ActorId),
        SurrealQuery.Of("IF $_local_actor = NONE { THROW 'ACTION_LOCAL_WITNESS_CHANGED' }"),
        SurrealQuery.Of("LET $_local_tenant = (UPDATE ONLY type::record('user', $_local_tenant_id) SET is_active = is_active WHERE $_local_delegated AND is_active = true RETURN AFTER)")
            .WithParam("_local_tenant_id", receipt.TenantId ?? receipt.ActorId)
            .WithParam("_local_delegated", receipt.TenantId is not null && receipt.TenantId != receipt.ActorId),
        SurrealQuery.Of("""
            LET $_local_grants = (UPDATE consent_grant SET revoked_at = revoked_at
                WHERE $_local_delegated AND grantor_user_id = type::record('user', $_local_actor_id)
                    AND tenant_id = type::record('user', $_local_tenant_id)
                    AND revoked_at = NONE AND (expires_at = NONE OR expires_at > time::now()) RETURN AFTER)
            """).WithParam("_local_actor_id", receipt.ActorId).WithParam("_local_tenant_id", receipt.TenantId ?? receipt.ActorId)
            .WithParam("_local_delegated", receipt.TenantId is not null && receipt.TenantId != receipt.ActorId),
        // No closures: strict SurrealForge validation reports |$x| parameters as unbound. Scope tokens contain no spaces.
        SurrealQuery.Of("LET $_local_scopes = array::flatten((SELECT VALUE string::split(string::replace(scopes, ' ', ''), ',') FROM $_local_grants))"),
        SurrealQuery.Of("""
            IF $_local_delegated AND ($_local_tenant = NONE OR !($_local_scopes CONTAINS $_local_scope)
                OR ($_local_wallet AND !($_local_scopes CONTAINS $_local_wallet_scope))) { THROW 'ACTION_LOCAL_WITNESS_CHANGED' }
            """).WithParam("_local_delegated", receipt.TenantId is not null && receipt.TenantId != receipt.ActorId)
            .WithParam("_local_wallet", wallet).WithParam("_local_scope", AzoaScopes.ActionExecute)
            .WithParam("_local_wallet_scope", AzoaScopes.WalletManage)
    ];
}
