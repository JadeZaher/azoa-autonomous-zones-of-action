using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Advances saved custody wire without deriving replacement authority from observation.</summary>
public sealed class HoldRailExecutor(IHoldCommandStore store, IBlockchainProviderFactory providers)
{
    /// <summary>Observes prior egress; new preparation requires current interactive authority or an exact persisted mandate.</summary>
    public async Task<AZOAResult<HoldCommand>> AdvanceAsync(HoldCommand row, bool allowPreparation, CancellationToken ct)
    {
        if (row.State is "Completed" or "Cancelled" or "FailedNoEffect") return AZOAResult<HoldCommand>.Success(row);
        if (row.RailPlanJson is null) return AZOAResult<HoldCommand>.Failure("The original custody plan is missing.");
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(row.RailPlanJson)!;
        var provider = providers.GetProvider(plan.Quote.Request.ChainType, plan.Quote.Request.Network);
        if (!provider.TryGetModule<IValueTransferTransactionModule>(out var module))
            return AZOAResult<HoldCommand>.Failure("The selected provider has no durable value transfer module.");
        if (row.AcceptanceState is not null and not "Confirmed")
        {
            var acceptance = await AdvanceAcceptanceAsync(row, false, ct);
            if (acceptance.IsError || acceptance.Result is null) return acceptance;
            row = acceptance.Result;
            if (row.AcceptanceState != "Confirmed") return AZOAResult<HoldCommand>.Success(row);
        }
        if (row.State is "Admitted" or "Preparing")
        {
            if (!allowPreparation) return AZOAResult<HoldCommand>.Success(row);
            if (row.StageIntentJson is null) return AZOAResult<HoldCommand>.Failure("Explicit quote consent is required.");
            if (!provider.TryGetModule<IValueCustodyModule>(out var custody))
                return AZOAResult<HoldCommand>.Failure("Current custody mechanics and recipient readiness are unavailable.");
            var current = await custody!.QuoteValueCustodyAsync(plan.Quote.Request, ct);
            if (current.IsError || current.Result is null)
                return AZOAResult<HoldCommand>.Failure(current.Message);
            if (current.Result.ProtocolIdentity != plan.Quote.ProtocolIdentity
                || current.Result.AssetAuthorityHash != plan.Quote.AssetAuthorityHash)
                return AZOAResult<HoldCommand>.Failure("The admitted asset mechanics changed; no new wire was authorized.");
            if (JsonSerializer.Deserialize<HoldCommandAuthority>(row.AuthorityJson)?.ReleaseMandateId is not null
                && (plan.Quote.TransferCosts is null || !HoldReleaseMandateIdentity.CostsWithin(current.Result, plan.Quote.TransferCosts)))
                return AZOAResult<HoldCommand>.Failure("Current custody costs exceed the immutable admitted release quote.");
            if (!current.Result.RecipientReady)
                return AZOAResult<HoldCommand>.Success(row, "The exact recipient must authorize asset acceptance before transfer preparation.");
            var claimed = await store.BeginPreparationAsync(row, ct);
            if (claimed is null) return await ReloadAsync(row.Id, ct);
            row = claimed;
            var intent = JsonSerializer.Deserialize<ValueTransferIntent>(row.StageIntentJson!)!;
            var prepared = await module!.PrepareValueTransferAsync(intent, ct);
            if (prepared.IsError || prepared.Result is null) return AZOAResult<HoldCommand>.Failure(prepared.Message);
            var saved = await store.SavePreparedAsync(row, prepared.Result, ct);
            if (saved is null) return await ReloadAsync(row.Id, ct);
            row = saved;
        }
        if (row.State == "Prepared")
        {
            if (!allowPreparation) return AZOAResult<HoldCommand>.Success(row);
            var admitted = await store.AuthorizeSendAsync(row, ct);
            if (admitted is null) return await ReloadAsync(row.Id, ct);
            row = admitted;
        }
        if (row.State != "Submitted") return AZOAResult<HoldCommand>.Success(row);
        if (row.PreparedJson is null) return AZOAResult<HoldCommand>.Failure("Authorized egress is missing its original signed bytes.");
        var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(row.PreparedJson)!;
        var observed = await module!.ObserveValueTransferAsync(wire, ct);
        if (observed.IsError) return AZOAResult<HoldCommand>.Failure(observed.Message);
        if (observed.Result == ChainConfirmation.Confirmed)
        {
            var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.PreparedJson), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.Confirmed, DateTimeOffset.UtcNow);
            var receipt = await store.CompleteRailAsync(row, evidence, ct);
            return receipt is null ? AZOAResult<HoldCommand>.Failure("Finalized custody evidence awaits atomic accounting reconciliation.")
                : await ReloadAsync(row.Id, ct);
        }
        if (observed.Result == ChainConfirmation.Unknown)
            await module.SubmitValueTransferAsync(wire, ct);
        if (observed.Result == ChainConfirmation.FailedOnChain)
        {
            var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.PreparedJson), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.FailedOnChain, DateTimeOffset.UtcNow);
            var recorded = await store.RecordNoEffectAsync(row, evidence, ct);
            return recorded is null ? await ReloadAsync(row.Id, ct) : AZOAResult<HoldCommand>.Success(recorded);
        }
        return AZOAResult<HoldCommand>.Success(row);
    }

    /// <summary>Advances participant wire; only an explicit participant command may request new custody or first send.</summary>
    public async Task<AZOAResult<HoldCommand>> AdvanceAcceptanceAsync(HoldCommand row, bool participantAuthorized, CancellationToken ct)
    {
        if (row.AcceptanceState is null or "Confirmed") return AZOAResult<HoldCommand>.Success(row);
        if (row.AcceptanceIntentJson is null) return AZOAResult<HoldCommand>.Failure("The original recipient consent is missing.");
        var intent = JsonSerializer.Deserialize<ValueAcceptanceIntent>(row.AcceptanceIntentJson)!;
        var provider = providers.GetProvider(intent.ChainType, intent.Network);
        if (!provider.TryGetModule<IValueCustodyModule>(out var module))
            return AZOAResult<HoldCommand>.Failure("The selected provider has no durable recipient acceptance module.");
        if (row.AcceptanceState is "Admitted" or "Preparing")
        {
            if (!participantAuthorized) return AZOAResult<HoldCommand>.Success(row);
            var claimed = await store.AdvanceAcceptanceAsync(row, "Preparing", ct: ct);
            if (claimed is null) return await ReloadAsync(row.Id, ct);
            row = claimed;
            var prepared = await module!.PrepareValueAcceptanceAsync(intent, ct);
            if (prepared.IsError || prepared.Result is null) return AZOAResult<HoldCommand>.Failure(prepared.Message);
            var saved = await store.AdvanceAcceptanceAsync(row, "Prepared", prepared.Result, ct: ct);
            if (saved is null) return await ReloadAsync(row.Id, ct);
            row = saved;
        }
        if (row.AcceptanceState == "Prepared")
        {
            if (!participantAuthorized) return AZOAResult<HoldCommand>.Success(row);
            var admitted = await store.AdvanceAcceptanceAsync(row, "Submitted", ct: ct);
            if (admitted is null) return await ReloadAsync(row.Id, ct);
            row = admitted;
        }
        if (row.AcceptanceState != "Submitted") return AZOAResult<HoldCommand>.Success(row);
        if (row.AcceptancePreparedJson is null) return AZOAResult<HoldCommand>.Failure("The original recipient wire is missing.");
        var wire = JsonSerializer.Deserialize<PreparedValueAcceptance>(row.AcceptancePreparedJson)!;
        var observed = await module!.ObserveValueAcceptanceAsync(wire, ct);
        if (observed.IsError) return AZOAResult<HoldCommand>.Failure(observed.Message);
        if (observed.Result == ChainConfirmation.Confirmed)
        {
            var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.AcceptancePreparedJson), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.Confirmed, DateTimeOffset.UtcNow);
            var completed = await store.AdvanceAcceptanceAsync(row, "Confirmed", evidence: evidence, ct: ct);
            return completed is null ? await ReloadAsync(row.Id, ct) : AZOAResult<HoldCommand>.Success(completed);
        }
        if (observed.Result == ChainConfirmation.Unknown) await module.SubmitValueAcceptanceAsync(wire, ct);
        if (observed.Result == ChainConfirmation.FailedOnChain)
        {
            var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.AcceptancePreparedJson), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.FailedOnChain, DateTimeOffset.UtcNow);
            var recorded = await store.AdvanceAcceptanceAsync(row, "FailedNoEffect", evidence: evidence, ct: ct);
            return recorded is null ? await ReloadAsync(row.Id, ct) : AZOAResult<HoldCommand>.Success(recorded);
        }
        return AZOAResult<HoldCommand>.Success(row);
    }

    private async Task<AZOAResult<HoldCommand>> ReloadAsync(string id, CancellationToken ct)
    {
        var current = await store.GetCommandAsync(id, ct);
        return current is null ? AZOAResult<HoldCommand>.Failure("The original custody command was not found.")
            : AZOAResult<HoldCommand>.Success(current);
    }
}
