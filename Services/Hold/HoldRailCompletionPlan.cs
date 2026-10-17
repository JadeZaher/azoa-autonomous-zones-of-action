using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Projects one finalized wire into its conserved original custody liability.</summary>
internal sealed record HoldRailCompletionPlan(HoldLedger Aggregate, HoldReserveMutationPlan? Reserve, HoldCommandReceipt Receipt)
{
    public static AZOAResult<HoldRailCompletionPlan> Create(HoldCommand command, HoldTransferEvidence evidence,
        HoldLedger aggregate, PlatformReserveLedger reserve)
    {
        if (command.State != "Submitted" || command.PreparedJson is null || command.RailPlanJson is null
            || command.StageIntentJson is null || command.Version is < 0 or long.MaxValue
            || aggregate.Version is < 0 or long.MaxValue || aggregate.ActiveCommandId != command.Id
            || SurrealRecordGuid.BareId(aggregate.Id) != command.HoldRecordId
            || evidence.Version != 1 || evidence.Confirmation != ChainConfirmation.Confirmed
            || evidence.PreparedHash != CanonicalJson.HashCanonical(command.PreparedJson)) return Fail();
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(command.IntentJson)!;
        var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(command.PreparedJson)!;
        var plan = JsonSerializer.Deserialize<HoldRailPlan>(command.RailPlanJson)!;
        if (command.IntentHash != CanonicalJson.Hash(intent) || plan.CommandId != command.Id
            || wire.TransactionId != evidence.TransactionId || wire.ProtocolIdentity != evidence.ProtocolIdentity
            || wire.ProtocolIdentity != intent.Asset.ProtocolIdentity
            || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire.Intent)) != command.StageIntentJson
            || !ValueCustodyIdentity.MatchesTransfer(wire.Intent) || wire.Intent.Cost!.CommandId != command.Id
            || wire.Intent.Cost.Generation != command.Generation || wire.Intent.Cost.Stage != plan.Kind
            || wire.Intent.Cost.Quote != plan.Quote
            || aggregate.DefinitionJson is null || aggregate.DefinitionHash != CanonicalJson.HashCanonical(aggregate.DefinitionJson)
            || aggregate.OwnerUserId != intent.OwnerUserId.ToString("N")
            || SurrealRecordGuid.ParseOptional(aggregate.TenantId) != intent.CustodyTenantId
            || aggregate.Amount != wire.Intent.QuantityBaseUnits || aggregate.ProtocolIdentity != wire.ProtocolIdentity
            || aggregate.AssetId != wire.Intent.AssetId || aggregate.Chain != wire.Intent.ChainType
            || aggregate.Network != wire.Intent.Network.ToString() || aggregate.HoldingAddress != intent.Asset.HoldingAddress
            || reserve.Chain != aggregate.Chain || reserve.Network != aggregate.Network
            || reserve.ProtocolIdentity != aggregate.ProtocolIdentity || reserve.AssetId != aggregate.AssetId
            || reserve.ReserveAddress != aggregate.HoldingAddress) return Fail();
        var definition = JsonSerializer.Deserialize<HoldDefinition>(aggregate.DefinitionJson)!;
        if (definition.Asset != intent.Asset || definition.QuantityBaseUnits != aggregate.Amount
            || definition.FundingSource != aggregate.FundingSource
            || definition.BeneficiaryUserId != SurrealRecordGuid.Parse(aggregate.BeneficiaryUserId)
            || definition.FunderUserId != SurrealRecordGuid.ParseOptional(aggregate.FunderUserId)) return Fail();
        var result = JsonSerializer.Deserialize<HoldLedger>(JsonSerializer.Serialize(aggregate))!;
        HoldReserveMutationPlan? mutation = null;
        if (plan.Kind == "Funding")
        {
            if (intent.Kind != "Open" || aggregate.State != "Pending" || definition.FundingSource != "Funder"
                || aggregate.OpeningCommandId != command.Id || intent.Definition != definition
                || plan.SenderWallet?.OwnerUserId != definition.FunderUserId
                || wire.Intent.Recipient != aggregate.HoldingAddress) return Fail();
            result.State = "Held";
        }
        else
        {
            if (aggregate.State != "Held" || wire.Intent.Sender != aggregate.HoldingAddress
                || plan.RecipientWallet is not { } recipient || wire.Intent.Recipient != recipient.Address) return Fail();
            if (plan.Kind == "Release" && intent.Kind == "Release" && recipient.OwnerUserId == definition.BeneficiaryUserId)
            {
                result.State = "Released";
                if (definition.FundingSource == "Reserve")
                {
                    var reserved = HoldReserveMutationPlan.Create(reserve, aggregate.ReservePartition,
                        ReserveDrawKind.ReleaseHold, aggregate.Amount, aggregate.ProtocolIdentity!);
                    if (reserved.IsError || reserved.Result is null) return Fail();
                    mutation = reserved.Result;
                }
            }
            else if (plan.Kind == "FundedRefund" && intent.Kind == "Refund" && definition.FundingSource == "Funder"
                && recipient.OwnerUserId == definition.FunderUserId) result.State = "Refunded";
            else return Fail();
            result.SettledAt = evidence.ObservedAt;
            result.SettlementReason = intent.Reason ?? plan.Kind;
        }
        result.ActiveCommandId = null;
        result.Version++;
        result.UpdatedAt = evidence.ObservedAt;
        result.SettlementTransactionId = evidence.TransactionId;
        var view = new HoldCommandView(command.Id, command.Version + 1, intent.Kind, "Completed", command.HoldRecordId,
            intent.HoldId, intent.Asset, aggregate.Amount, intent.ActorUserId, intent.OwnerUserId, intent.TenantId,
            definition.BeneficiaryUserId, evidence.TransactionId, null, CustodyTenantId: intent.CustodyTenantId);
        var receipt = new HoldCommandReceipt { Id = command.Id, HoldRecordId = command.HoldRecordId,
            IntentHash = command.IntentHash, ActorUserId = command.ActorUserId, TenantId = command.TenantId,
            OutcomeJson = JsonSerializer.Serialize(view), EvidenceJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence)),
            CreatedAt = evidence.ObservedAt };
        return AZOAResult<HoldRailCompletionPlan>.Success(new(result, mutation, receipt));
    }

    private static AZOAResult<HoldRailCompletionPlan> Fail()
        => AZOAResult<HoldRailCompletionPlan>.Failure("Finalized evidence must match the exact admitted custody liability and saved wire.");
}
