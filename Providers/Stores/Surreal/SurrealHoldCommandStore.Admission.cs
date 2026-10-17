using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public Task<HoldCommand?> GetCommandAsync(string operationId, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldCommand>(SurrealQuery<HoldCommand>.Key(operationId), ct);

    /// <inheritdoc/>
    public async Task<HoldCommand?> ClaimRailAsync(HoldCommandIntent intent, HoldRailPlan plan, HoldCommandAuthority authority,
        CancellationToken ct = default)
    {
        var id = HoldCommandIdentity.Operation(intent);
        var hash = CanonicalJson.Hash(intent);
        var prior = await GetCommandAsync(id, ct);
        if (prior is not null) return SameRail(prior, intent, plan, hash) ? prior : null;
        var fences = await RailAuthorityAsync(intent, plan, authority, ct);
        if (fences is null) return null;
        var reserveId = PlatformReserveLedger.RecordIdFor(intent.Asset.ChainType, intent.Asset.Network.ToString(), intent.Asset.AssetId);
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(reserveId), ct);
        if (reserve is null) return null;
        var current = await GetHoldAsync(HoldCommandIdentity.Hold(intent), ct);
        if (current is not null)
        {
            if (current.OpeningCommandId is null) return null;
            var receipt = await GetReceiptAsync(current.OpeningCommandId, ct);
            if (receipt is null || !ProvesLiability(receipt, current, intent)) return null;
        }
        var planned = HoldRailAdmissionPlan.Create(intent, plan, authority, current, reserve, DateTimeOffset.UtcNow);
        if (planned.IsError || planned.Result is not { } admission)
        {
            prior = await GetCommandAsync(id, ct);
            return prior is not null && SameRail(prior, intent, plan, hash) ? prior : null;
        }
        if (authority.ReleaseMandateId is { } acceptedMandate)
        {
            admission.Command.State = "Admitted";
            admission.Command.AcceptedAt = admission.Command.CreatedAt;
            admission.Command.AcceptedQuoteId = plan.Quote.QuoteId;
            admission.Command.CurrentAuthorizationId = acceptedMandate;
            admission.Command.CurrentAuthorityJson = admission.Command.AuthorityJson;
            admission.Command.StageIntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(
                new ValueTransferIntent(intent.Asset.ChainType, intent.Asset.Network, intent.Asset.AssetId,
                    plan.Quote.Request.Sender, plan.Quote.Request.Recipient, plan.Quote.Request.QuantityBaseUnits,
                    plan.SenderSigningContext, $"hold:{id}:{plan.Kind}:0",
                    new(1, id, plan.Kind, 0, plan.Quote, plan.Quote.Request.Sender, plan.Quote.TransferNetworkFeeBaseUnits))));
        }
        // raw: original quote, current authority and one reserved custody liability commit before any preparation.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        if (authority.ReleaseMandateId is { } mandateId)
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldReleaseMandate>(mandateId)
                .Where(row => row.Status == "Active" && row.OperationId == null)
                .Set(row => row.OperationId, id).Set(row => row.UpdatedAt, admission.Command.CreatedAt).Build(),
                "hold_mandate_operation", "_hold_mandate_operation"));
            statements.Add(SurrealQuery.Of("IF $_hold_mandate_operation = NONE OR $_hold_mandate_operation = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        statements.Add(SurrealQuery.Of("IF $_hold_quote_expiry <= time::now() { THROW 'HOLD_COMMAND_CHANGED' }")
            .WithParam("_hold_quote_expiry", plan.Quote.ExpiresAt));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserveId)
            .Where(row => row.Version == reserve.Version && row.Chain == reserve.Chain && row.Network == reserve.Network
                && row.ProtocolIdentity == reserve.ProtocolIdentity && row.AssetId == reserve.AssetId
                && row.ReserveAddress == reserve.ReserveAddress && row.TenantId == reserve.TenantId)
            .Set(row => row.Version, reserve.Version).Build(), "hold_rail_reserve", "_hold_rail_reserve"));
        statements.Add(SurrealQuery.Of("IF $_hold_rail_reserve = NONE OR $_hold_rail_reserve = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        if (plan.RecipientWallet is { } recipient)
        {
            var owner = SurrealLink.ToLink("user", recipient.OwnerUserId.ToString("N"))!;
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Wallet>(recipient.Id.ToString("N"))
                .Where(row => row.UserId == owner && row.Address == recipient.Address && row.ChainType == recipient.ChainType)
                .Set(row => row.Address, recipient.Address).Build(), "hold_rail_recipient", "_hold_rail_recipient"));
            statements.Add(SurrealQuery.Of("IF $_hold_rail_recipient = NONE OR $_hold_rail_recipient = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        if (admission.CreatesAggregate)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(admission.Aggregate), "hold_rail_open"));
        else
        {
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(admission.Aggregate.Id)
                .Where(row => row.Version == current!.Version && row.State == "Held" && row.ActiveCommandId == null
                    && row.DefinitionHash == current.DefinitionHash && row.DefinitionJson == current.DefinitionJson
                    && row.OwnerUserId == current.OwnerUserId && row.TenantId == current.TenantId
                    && row.OpeningCommandId == current.OpeningCommandId)
                .Set(row => row.Version, admission.Aggregate.Version).Set(row => row.ActiveCommandId, id)
                .Set(row => row.UpdatedAt, admission.Aggregate.UpdatedAt).Build(), "hold_rail_claim", "_hold_rail_claim"));
            statements.Add(SurrealQuery.Of("IF $_hold_rail_claim = NONE OR $_hold_rail_claim = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(admission.Command), "hold_rail_command"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk(); return response;
            }, ct);
            return admission.Command;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(Conflict, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await GetCommandAsync(id, ct);
            return prior is not null && SameRail(prior, intent, plan, hash) ? prior : null;
        }
    }

    private static bool SameRail(HoldCommand row, HoldCommandIntent intent, HoldRailPlan plan, string hash)
        => row.ActorUserId == intent.ActorUserId.ToString("N") && row.TenantId == intent.TenantId?.ToString("N")
            && row.IntentHash == hash && row.RailPlanJson == CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));

    private async Task<IReadOnlyList<SurrealQuery>?> RailAuthorityAsync(HoldCommandIntent intent, HoldRailPlan plan,
        HoldCommandAuthority authority, CancellationToken ct)
    {
        if (authority.ReleaseMandateId is { } mandateId)
            return await MandateAuthorityAsync(intent, plan, authority, mandateId, ct);
        var local = LocalAuthority(intent, authority);
        if (local is null) return null;
        if (plan.SenderWallet is null) return authority.Sender is null ? local : null;
        if (authority.Sender is not { } sender || sender.Wallet != plan.SenderWallet
            || sender.Credential.ActorUserId != intent.ActorUserId || sender.Credential.TenantId != intent.TenantId
            || CanonicalJson.Hash(sender.Credential) != CanonicalJson.Hash(authority.Direct?.Credential ?? authority.Workflow!.Credential)
            || CanonicalJson.Hash(sender.Grants) != CanonicalJson.Hash(authority.Direct?.Grants ?? authority.Workflow!.Grants)) return null;
        var fence = SigningAuthorityFence.Create(sender, [AzoaScopes.TransferSign], "hold_sender");
        return fence;
    }
}
