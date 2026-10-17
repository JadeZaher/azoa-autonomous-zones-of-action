using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Reserves one exact custody movement without declaring any chain effect completed.</summary>
internal sealed record HoldRailAdmissionPlan(HoldCommand Command, HoldLedger Aggregate, bool CreatesAggregate)
{
    public static AZOAResult<HoldRailAdmissionPlan> Create(HoldCommandIntent intent, HoldRailPlan plan,
        HoldCommandAuthority authority, HoldLedger? current, PlatformReserveLedger reserve, DateTimeOffset now)
    {
        if (intent.Version != 1 || intent.ActorUserId == Guid.Empty || intent.OwnerUserId == Guid.Empty
            || intent.TenantId == Guid.Empty || intent.CustodyTenantId == Guid.Empty
            || string.IsNullOrWhiteSpace(intent.RequestKey) || intent.RequestKey.Length > 200 || intent.RequestKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.HoldId) || intent.HoldId.Length > 200
            || intent.HoldId != intent.HoldId.Trim() || intent.HoldId.Any(char.IsControl)
            || intent.Asset is null || intent.NextHoldId is not null || intent.NextBeneficiaryUserId is not null
            || intent.NextReleaseNotBefore is not null || intent.NextAutoRelease
            || plan.Version != 1 || plan.Generation != 0 || plan.CommandId != HoldCommandIdentity.Operation(intent)
            || !ValueCustodyIdentity.ValidQuote(plan.Quote) || plan.Quote.ProtocolIdentity != intent.Asset.ProtocolIdentity
            || plan.Quote.Request.ChainType != intent.Asset.ChainType || plan.Quote.Request.Network != intent.Asset.Network
            || plan.Quote.Request.AssetId != intent.Asset.AssetId || plan.Quote.Request.HoldingAddress != intent.Asset.HoldingAddress
            || reserve.Chain != intent.Asset.ChainType || reserve.Network != intent.Asset.Network.ToString()
            || reserve.ProtocolIdentity != intent.Asset.ProtocolIdentity || reserve.AssetId != intent.Asset.AssetId
            || reserve.ReserveAddress != intent.Asset.HoldingAddress)
            return Fail("Exact command, quote and physical reserve identity are required.");
        HoldLedger aggregate;
        var creates = plan.Kind == "Funding";
        if (creates)
        {
            if (intent.Kind != "Open" || current is not null || intent.Definition is not { } definition
                || definition.Asset != intent.Asset || definition.FundingSource != "Funder"
                || definition.FunderUserId != intent.ActorUserId || intent.OwnerUserId != intent.ActorUserId
                || intent.CustodyTenantId != intent.TenantId || definition.BeneficiaryUserId == Guid.Empty
                || definition.ReservePartition is not null || definition.AutoRelease
                || definition.QuantityBaseUnits != plan.Quote.Request.QuantityBaseUnits
                || plan.SenderWallet is not { } wallet || wallet.OwnerUserId != intent.ActorUserId
                || wallet.Address != plan.Quote.Request.Sender || wallet.ChainType != intent.Asset.ChainType
                || plan.RecipientWallet is not null || plan.Quote.Request.Recipient != intent.Asset.HoldingAddress
                || plan.SenderSigningContext.IsPlatform || plan.SenderSigningContext.UserId != wallet.OwnerUserId
                || plan.SenderSigningContext.WalletId != wallet.Id)
                return Fail("Funded opening requires the actual funder's exact inbound wallet and liability.");
            aggregate = HoldAggregateProjection.Create(intent, intent.HoldId, definition, plan.CommandId, now, "Pending");
            aggregate.ActiveCommandId = plan.CommandId;
        }
        else
        {
            if (current is null || current.State != "Held" || current.ActiveCommandId is not null
                || current.Version is < 0 or long.MaxValue || current.OpeningCommandId is null
                || SurrealRecordGuid.BareId(current.Id) != HoldCommandIdentity.Hold(intent)
                || current.OwnerUserId != intent.OwnerUserId.ToString("N")
                || SurrealRecordGuid.ParseOptional(current.TenantId) != intent.CustodyTenantId
                || current.ProtocolIdentity != intent.Asset.ProtocolIdentity || current.AssetId != intent.Asset.AssetId
                || current.HoldingAddress != intent.Asset.HoldingAddress || current.Amount != plan.Quote.Request.QuantityBaseUnits
                || current.DefinitionJson is null || current.DefinitionHash != CanonicalJson.HashCanonical(current.DefinitionJson)
                || intent.Definition is not null || plan.SenderWallet is not null || !plan.SenderSigningContext.IsPlatform
                || plan.Quote.Request.Sender != intent.Asset.HoldingAddress || plan.RecipientWallet is not { } recipient
                || recipient.Address != plan.Quote.Request.Recipient || recipient.ChainType != intent.Asset.ChainType)
                return Fail("Settlement requires the unchanged admitted liability and exact recipient account.");
            var definition = JsonSerializer.Deserialize<HoldDefinition>(current.DefinitionJson)!;
            if (definition.QuantityBaseUnits != current.Amount || definition.Asset != intent.Asset
                || definition.FundingSource != current.FundingSource || definition.ReleaseNotBefore != current.ReleaseNotBefore
                || definition.BeneficiaryUserId != SurrealRecordGuid.Parse(current.BeneficiaryUserId)
                || definition.FunderUserId != SurrealRecordGuid.ParseOptional(current.FunderUserId))
                return Fail("Stored custody liability differs from its original definition.");
            if (plan.Kind == "Release")
            {
                if (intent.Kind != "Release" || recipient.OwnerUserId != definition.BeneficiaryUserId
                    || (intent.ActorUserId != definition.BeneficiaryUserId && intent.ActorUserId != intent.OwnerUserId))
                    return Fail("Only the original owner or beneficiary may release to the admitted beneficiary.");
            }
            else if (plan.Kind == "FundedRefund")
            {
                if (intent.Kind != "Refund" || definition.FundingSource != "Funder"
                    || definition.FunderUserId != recipient.OwnerUserId || intent.ActorUserId != definition.FunderUserId)
                    return Fail("Funded refunds return only to the original actual funder.");
            }
            else return Fail("Unknown custody movement kind.");
            aggregate = JsonSerializer.Deserialize<HoldLedger>(JsonSerializer.Serialize(current))!;
            aggregate.ActiveCommandId = plan.CommandId;
            aggregate.Version++;
            aggregate.UpdatedAt = now;
        }
        var command = new HoldCommand { Id = plan.CommandId, ActorUserId = intent.ActorUserId.ToString("N"),
            RecipientUserId = plan.RecipientWallet?.OwnerUserId.ToString("N"),
            TenantId = intent.TenantId?.ToString("N"), HoldRecordId = HoldCommandIdentity.Hold(intent), Kind = intent.Kind,
            IntentHash = CanonicalJson.Hash(intent), IntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent)),
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            RailPlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)),
            State = "AwaitingConsent", Stage = plan.Kind, CreatedAt = now, UpdatedAt = now };
        return AZOAResult<HoldRailAdmissionPlan>.Success(new(command, aggregate, creates));
    }

    private static AZOAResult<HoldRailAdmissionPlan> Fail(string message) => AZOAResult<HoldRailAdmissionPlan>.Failure(message);
}
