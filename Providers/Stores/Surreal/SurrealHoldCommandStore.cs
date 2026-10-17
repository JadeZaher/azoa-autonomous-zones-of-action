using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealHoldCommandStore(ISurrealExecutor executor, ReserveOptions options) : IHoldCommandStore, IExchangeHoldSourceStore
{
    private const string Conflict = "HOLD_COMMAND_CHANGED";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HoldCommand>> ListParticipantAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
    {
        var actorId = actor.ToString("N"); var tenantId = tenant?.ToString("N");
        var query = SurrealQuery<HoldCommand>.From();
        query = tenant is null ? query.Where(row => (row.ActorUserId == actorId && row.TenantId == null) || row.RecipientUserId == actorId)
            : query.Where(row => row.ActorUserId == actorId && row.TenantId == tenantId);
        return await executor.QueryAsync<HoldCommand>(query.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id)
            .Start(Math.Max(0, offset)).Limit(Math.Clamp(limit, 1, 100)), ct);
    }

    /// <inheritdoc/>
    public Task<HoldCommandReceipt?> GetReceiptAsync(string operationId, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldCommandReceipt>(SurrealQuery<HoldCommandReceipt>.Key(operationId), ct);

    /// <inheritdoc/>
    public Task<HoldRecoveryReceipt?> GetRecoveryAsync(string receiptId, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldRecoveryReceipt>(SurrealQuery<HoldRecoveryReceipt>.Key(receiptId), ct);

    /// <inheritdoc/>
    public Task<HoldCommandAuthorization?> GetAuthorizationAsync(string receiptId, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldCommandAuthorization>(SurrealQuery<HoldCommandAuthorization>.Key(receiptId), ct);

    /// <inheritdoc/>
    public Task<HoldLedger?> GetHoldAsync(string holdRecordId, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldLedger>(SurrealQuery<HoldLedger>.Key(holdRecordId), ct);

    /// <inheritdoc/>
    public async Task<HoldLedger?> FindOwnedHoldAsync(Guid owner, Guid? tenant, string chain, ChainNetwork network,
        string holdId, CancellationToken ct = default)
    {
        var actor = owner.ToString("N"); var scope = SurrealLink.ToLink("user", tenant?.ToString("N"));
        var networkName = network.ToString();
        var rows = await executor.QueryAsync<HoldLedger>(SurrealQuery<HoldLedger>.From()
            .Where(row => row.OwnerUserId == actor && row.TenantId == scope && row.Chain == chain
                && row.Network == networkName && row.HoldId == holdId).Limit(2), ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <inheritdoc/>
    public Task<HoldCommandReceipt?> CommitLocalAsync(HoldCommandIntent intent, HoldCommandAuthority authority,
        CancellationToken ct = default)
        => CommitLocalCoreAsync(intent, authority, null, ct);

    private async Task<HoldCommandReceipt?> CommitLocalCoreAsync(HoldCommandIntent intent, HoldCommandAuthority authority,
        WorkflowOpeningCommit? opening, CancellationToken ct, ExchangeOpeningCommit? exchange = null)
    {
        if (intent.Asset is null || intent.ActorUserId == Guid.Empty || intent.OwnerUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(intent.RequestKey)) return null;
        var id = HoldCommandIdentity.Operation(intent);
        var hash = CanonicalJson.Hash(intent);
        var prior = await GetReceiptAsync(id, ct);
        if (prior is not null) return Matches(prior, intent, hash) ? prior : null;
        if (opening is null && exchange is null && (intent.Definition?.AutoRelease == true || intent.NextAutoRelease)) return null;
        var fences = exchange is null ? LocalAuthority(intent, authority) : ExchangeOpeningFence(exchange, intent, authority);
        if (fences is null) return null;
        var reserveId = PlatformReserveLedger.RecordIdFor(intent.Asset.ChainType, intent.Asset.Network.ToString(), intent.Asset.AssetId);
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(reserveId), ct);
        if (reserve is null) return null;
        if (intent.Kind == "Open")
        {
            var credential = exchange?.Authority.Tenant.Credential ?? authority.Direct?.Credential ?? authority.Workflow!.Credential;
            var tenant = credential.TenantId ?? (SurrealRecordGuid.ParseOptional(reserve.TenantId) == credential.ActorUserId
                ? credential.ActorUserId : (Guid?)null);
            var drawAuthority = credential.TokenUse == AzoaClaims.TokenUseNodeOperator
                ? ReserveDrawAuthority.NodeOperator : ReserveDrawAuthority.ForTenant(tenant);
            if (ReservePartitionScope.Refuse(reserve, drawAuthority, reserve.AssetId, options) is not null) return null;
        }
        var current = await GetHoldAsync(HoldCommandIdentity.Hold(intent), ct);
        HoldCommandReceipt? original = null;
        if (current is not null)
        {
            if (current.OpeningCommandId is null) return null;
            original = await GetReceiptAsync(current.OpeningCommandId, ct);
            if (original is null || !ProvesLiability(original, current, intent)) return null;
        }
        var now = DateTimeOffset.UtcNow;
        var planned = HoldLocalCommandPlan.Create(intent, current, reserve, now);
        if (planned.IsError || planned.Result is not { } plan)
        {
            prior = await GetReceiptAsync(id, ct);
            return prior is not null && Matches(prior, intent, hash) ? prior : null;
        }
        var openingComposition = opening is null ? null : ComposeWorkflowOpening(intent, authority, opening, plan, now);
        if (opening is not null && openingComposition is null) return null;
        var exchangeComposition = exchange?.Intent.ReleaseConsent is null ? null : ComposeExchangeMandate(exchange, plan, now);
        if (exchange?.Intent.Opening.Definition?.AutoRelease == true && exchangeComposition is null) return null;
        if (exchange is not null)
        {
            plan.Result.EconomicSourceId = exchange.Source.Id;
            plan.Result.State = "AwaitingSourceSettlement";
        }
        var command = new HoldCommand { Id = id, ActorUserId = intent.ActorUserId.ToString("N"),
            TenantId = intent.TenantId?.ToString("N"), HoldRecordId = plan.Receipt.HoldRecordId,
            Kind = intent.Kind, IntentHash = hash,
            IntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent)),
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            State = "Completed", CreatedAt = now, UpdatedAt = now };
        // raw: current authority, liability transitions, conserved reserve counters and immutable receipt commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        if (openingComposition is not null) statements.AddRange(openingComposition);
        if (exchangeComposition is not null) statements.AddRange(exchangeComposition);
        var reserveWrite = SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserveId)
            .Where(row => row.Version == reserve.Version && row.Chain == reserve.Chain && row.Network == reserve.Network
                && row.ProtocolIdentity == reserve.ProtocolIdentity && row.AssetId == reserve.AssetId
                && row.ReserveAddress == reserve.ReserveAddress && row.TenantId == reserve.TenantId
                && row.TotalSupply == reserve.TotalSupply && row.Decimals == reserve.Decimals && row.UnitName == reserve.UnitName)
            .Set(row => row.Version, plan.Reserve is null ? reserve.Version : reserve.Version + 1);
        if (plan.Reserve is { } mutation)
            reserveWrite = reserveWrite.Set(row => row.Partitions, mutation.Partitions).Set(row => row.UpdatedAt, now);
        statements.Add(SurrealGeneratedStatementScope.Assign(reserveWrite.Build(), "hold_reserve", "_hold_reserve"));
        statements.Add(SurrealQuery.Of("IF $_hold_reserve = NONE OR $_hold_reserve = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        if (current is null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(plan.Result), "hold_open"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<HoldCommandReceipt>.Key(current.OpeningCommandId!),
                "hold_original_receipt", "_hold_original_receipt"));
            statements.Add(SurrealQuery.Of("""
                IF $_hold_original_receipt = NONE OR $_hold_original_receipt.intent_hash != $_hold_original_hash
                    OR $_hold_original_receipt.outcome_json != $_hold_original_outcome { THROW 'HOLD_COMMAND_CHANGED' }
                """).WithParam("_hold_original_hash", original!.IntentHash).WithParam("_hold_original_outcome", original.OutcomeJson));
            var update = SurrealWriter.UpdateOnly<HoldLedger>(plan.Result.Id)
                .Where(row => row.Version == current.Version && row.State == "Held" && row.ActiveCommandId == null
                    && row.DefinitionHash == current.DefinitionHash && row.DefinitionJson == current.DefinitionJson
                    && row.OpeningCommandId == current.OpeningCommandId && row.OwnerUserId == current.OwnerUserId
                    && row.TenantId == current.TenantId)
                .Set(row => row.Version, plan.Result.Version).Set(row => row.State, plan.Result.State)
                .Set(row => row.UpdatedAt, now).Set(row => row.SettledAt, now);
            update = plan.Result.SettlementReason is null ? update.Unset(row => row.SettlementReason)
                : update.Set(row => row.SettlementReason, plan.Result.SettlementReason);
            update = plan.Result.ChildHoldId is null ? update.Unset(row => row.ChildHoldId)
                : update.Set(row => row.ChildHoldId, plan.Result.ChildHoldId);
            statements.Add(SurrealGeneratedStatementScope.Assign(update.Build(), "hold_transition", "_hold_transition"));
            statements.Add(SurrealQuery.Of("IF $_hold_transition = NONE OR $_hold_transition = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        if (plan.Child is not null) statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(plan.Child), "hold_child"));
        if (opening is null) statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(command), "hold_command"));
        else
        {
            var expected = opening.Expected;
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldCommand>(id)
                .Where(row => row.Version == expected.Version && row.State == "AwaitingConsent" && row.IntentHash == expected.IntentHash
                    && row.AuthorityJson == expected.AuthorityJson && row.WorkflowMandateDraftJson == expected.WorkflowMandateDraftJson)
                .Set(row => row.Version, expected.Version + 1).Set(row => row.State, "Completed").Set(row => row.UpdatedAt, now)
                .Set(row => row.CurrentAuthorizationId, opening.AuditId).Build(), "hold_open_accepted", "_hold_open_accepted"));
            statements.Add(SurrealQuery.Of("IF $_hold_open_accepted = NONE OR $_hold_open_accepted = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(plan.Receipt), "hold_receipt"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return plan.Receipt;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ExchangeAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await GetReceiptAsync(id, ct);
            return prior is not null && Matches(prior, intent, hash) ? prior : null;
        }
    }

    private static IReadOnlyList<SurrealQuery>? LocalAuthority(HoldCommandIntent intent, HoldCommandAuthority authority)
    {
        if (authority.Version != 1 || authority.EconomicSource is not null || authority.ReleaseMandateId is not null
            || (authority.Direct is null) == (authority.Workflow is null)) return null;
        SigningCredentialWitness credential;
        IReadOnlyList<SurrealQuery>? fences;
        if (authority.Direct is { } direct)
        {
            credential = direct.Credential;
            fences = SigningAuthorityFence.ForConsent(direct, [AzoaScopes.TransferSign], "hold_auth");
        }
        else
        {
            var workflow = authority.Workflow!;
            credential = workflow.Credential;
            if (!workflow.RequiredScopes.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { AzoaScopes.ActionExecute, AzoaScopes.TransferSign }.Order(StringComparer.Ordinal))) return null;
            var selected = SigningAuthorityFence.ForNodeAdmission(workflow, "hold_auth");
            if (selected is null) return null;
            var statements = selected.ToList();
            statements.AddRange(ActionExecutionFence.Create(workflow.Action.RunId.ToString("N"), workflow.Action.NodeId.ToString("N"),
                intent.ActorUserId.ToString("N"), intent.TenantId?.ToString("N"), workflow.Action.IntentHash));
            statements.Add(SurrealQuery.Of("""
                IF $_effect_execution[0].signing_authority_json = NONE
                    OR crypto::sha256($_effect_execution[0].signing_authority_json) != $_hold_witness_hash { THROW 'HOLD_COMMAND_CHANGED' }
                """).WithParam("_hold_witness_hash", CanonicalJson.Hash(workflow)));
            fences = statements;
        }
        return credential.ActorUserId == intent.ActorUserId && credential.TenantId == intent.TenantId ? fences : null;
    }

    private static bool Matches(HoldCommandReceipt receipt, HoldCommandIntent intent, string hash)
        => receipt.IntentHash == hash && receipt.ActorUserId == intent.ActorUserId.ToString("N")
            && receipt.TenantId == intent.TenantId?.ToString("N");

    private static bool ProvesLiability(HoldCommandReceipt receipt, HoldLedger row, HoldCommandIntent intent)
    {
        var outcome = JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson);
        var rowId = SurrealRecordGuid.BareId(row.Id);
        return outcome is { State: "Completed" } && outcome.OperationId == row.OpeningCommandId
            && (outcome.HoldRecordId == rowId || outcome.NextHoldRecordId == rowId)
            && outcome.Asset == intent.Asset && outcome.QuantityBaseUnits == row.Amount
            && outcome.OwnerUserId == intent.OwnerUserId && outcome.CustodyTenantId == intent.CustodyTenantId;
    }
}
