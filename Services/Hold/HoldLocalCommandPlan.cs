using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Builds local hold transitions for a single atomic authority/accounting transaction.</summary>
public sealed record HoldLocalCommandPlan(HoldLedger Result, HoldLedger? Child,
    HoldReserveMutationPlan? Reserve, HoldCommandReceipt Receipt)
{
    /// <summary>Plans reserve opening, reserve refund, or custody-preserving chaining without writing.</summary>
    public static AZOAResult<HoldLocalCommandPlan> Create(HoldCommandIntent intent, HoldLedger? current,
        PlatformReserveLedger reserve, DateTimeOffset now)
    {
        if (intent.Version != 1 || intent.ActorUserId == Guid.Empty || intent.OwnerUserId == Guid.Empty
            || intent.TenantId == Guid.Empty || intent.CustodyTenantId == Guid.Empty || string.IsNullOrWhiteSpace(intent.RequestKey)
            || intent.RequestKey.Length > 200 || intent.RequestKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.HoldId) || intent.HoldId.Length > 200
            || intent.HoldId != intent.HoldId.Trim() || intent.HoldId.Any(char.IsControl)
            || intent.Asset is null || !Enum.IsDefined(intent.Asset.Network)
            || string.IsNullOrWhiteSpace(intent.Asset.ProtocolIdentity)
            || reserve.Version is < 0 or long.MaxValue
            || reserve.Chain != intent.Asset.ChainType || reserve.Network != intent.Asset.Network.ToString()
            || reserve.ProtocolIdentity != intent.Asset.ProtocolIdentity || reserve.AssetId != intent.Asset.AssetId
            || reserve.ReserveAddress != intent.Asset.HoldingAddress)
            return Fail("The exact command, owner and physical custody identity are required.");
        var operationId = HoldCommandIdentity.Operation(intent);
        var holdRecordId = HoldCommandIdentity.Hold(intent);
        HoldLedger result;
        HoldLedger? child = null;
        HoldReserveMutationPlan? mutation = null;
        if (intent.Kind == "Open")
        {
            if (current is not null || intent.Definition is not { } definition || definition.Asset != intent.Asset
                || definition.FundingSource != "Reserve" || definition.FunderUserId is not null
                || definition.BeneficiaryUserId == Guid.Empty || string.IsNullOrWhiteSpace(definition.ReservePartition)
                || intent.ActorUserId != intent.OwnerUserId
                || (intent.TenantId != intent.CustodyTenantId && intent.ActorUserId != intent.CustodyTenantId)
                || intent.NextHoldId is not null
                || intent.NextBeneficiaryUserId is not null || intent.NextReleaseNotBefore is not null || intent.NextAutoRelease)
                return Fail("A new reserve hold requires its original owner's complete immutable definition.");
            var planned = HoldReserveMutationPlan.Create(reserve, definition.ReservePartition, ReserveDrawKind.Hold,
                definition.QuantityBaseUnits, intent.Asset.ProtocolIdentity);
            if (planned.IsError || planned.Result is null) return Fail(planned.Message);
            mutation = planned.Result;
            result = HoldAggregateProjection.Create(intent, intent.HoldId, definition, operationId, now);
        }
        else
        {
            if (current is null || SurrealRecordGuid.BareId(current.Id) != holdRecordId
                || current.Version is < 0 or long.MaxValue || current.State != "Held"
                || current.ActiveCommandId is not null || current.OpeningCommandId is null
                || current.DefinitionJson is null || current.DefinitionHash is null
                || current.DefinitionHash != CanonicalJson.HashCanonical(current.DefinitionJson)
                || current.OwnerUserId != intent.OwnerUserId.ToString("N")
                || SurrealRecordGuid.ParseOptional(current.TenantId) != intent.CustodyTenantId
                || current.Chain != intent.Asset.ChainType || current.Network != intent.Asset.Network.ToString()
                || current.ProtocolIdentity != intent.Asset.ProtocolIdentity || current.AssetId != intent.Asset.AssetId
                || current.HoldingAddress != intent.Asset.HoldingAddress || intent.Definition is not null)
                return Fail("The unchanged held aggregate and original admission provenance are required.");
            if (current.EconomicSourceId is not null)
                return Fail("A purchased liability settles only through its original economic source or a release to its beneficiary.");
            var definition = JsonSerializer.Deserialize<HoldDefinition>(current.DefinitionJson);
            if (definition is null || definition.Asset != intent.Asset || definition.QuantityBaseUnits != current.Amount
                || definition.FundingSource != current.FundingSource || definition.ReservePartition != current.ReservePartition
                || definition.BeneficiaryUserId != SurrealRecordGuid.Parse(current.BeneficiaryUserId)
                || definition.FunderUserId != SurrealRecordGuid.ParseOptional(current.FunderUserId)
                || definition.ReleaseNotBefore != current.ReleaseNotBefore || definition.AutoRelease != current.AutoRelease)
                return Fail("The stored hold no longer matches its admitted liability.");
            result = JsonSerializer.Deserialize<HoldLedger>(JsonSerializer.Serialize(current))!;
            result.Version++;
            result.UpdatedAt = now;
            result.SettledAt = now;
            result.SettlementReason = intent.Reason;
            if (intent.Kind == "Refund")
            {
                if (current.FundingSource != "Reserve" || intent.ActorUserId != intent.OwnerUserId
                    || !HoldCustodyScope.AdmitsSettlement(intent) || intent.NextHoldId is not null || intent.NextBeneficiaryUserId is not null
                    || intent.NextReleaseNotBefore is not null || intent.NextAutoRelease)
                    return Fail("A local refund returns the original owner's reserve commitment only.");
                var planned = HoldReserveMutationPlan.Create(reserve, current.ReservePartition, ReserveDrawKind.CancelHold,
                    current.Amount, intent.Asset.ProtocolIdentity);
                if (planned.IsError || planned.Result is null) return Fail(planned.Message);
                mutation = planned.Result;
                result.State = "Refunded";
            }
            else if (intent.Kind == "ReleaseIntoHold")
            {
                if (intent.ActorUserId != intent.OwnerUserId || intent.TenantId != intent.CustodyTenantId || string.IsNullOrWhiteSpace(intent.NextHoldId)
                    || intent.NextHoldId.Length > 200 || intent.NextHoldId != intent.NextHoldId.Trim()
                    || intent.NextHoldId.Any(char.IsControl) || intent.NextHoldId == intent.HoldId
                    || intent.NextBeneficiaryUserId is null || intent.NextBeneficiaryUserId == Guid.Empty
                    || (current.ReleaseNotBefore is { } earliest
                        && (intent.NextReleaseNotBefore is null || intent.NextReleaseNotBefore < earliest)))
                    return Fail("A child hold must preserve its original owner's custody and cannot shorten the window.");
                var nextDefinition = definition with { BeneficiaryUserId = intent.NextBeneficiaryUserId.Value,
                    ReleaseNotBefore = intent.NextReleaseNotBefore, AutoRelease = intent.NextAutoRelease };
                child = HoldAggregateProjection.Create(intent, intent.NextHoldId, nextDefinition, operationId, now);
                child.ParentHoldId = current.HoldId;
                child.InheritsReserveCommitment = current.FundingSource == "Reserve";
                result.State = "ReleasedIntoHold";
                result.ChildHoldId = child.HoldId;
            }
            else return Fail("This command requires the durable rail lifecycle rather than a local accounting transition.");
        }
        var view = new HoldCommandView(operationId, 0, intent.Kind, "Completed", holdRecordId, intent.HoldId,
            intent.Asset, result.Amount, intent.ActorUserId, intent.OwnerUserId, intent.TenantId,
            SurrealRecordGuid.Parse(result.BeneficiaryUserId), null, null, child?.Id, intent.CustodyTenantId);
        var receipt = new HoldCommandReceipt { Id = operationId, HoldRecordId = holdRecordId,
            IntentHash = CanonicalJson.Hash(intent), ActorUserId = intent.ActorUserId.ToString("N"),
            TenantId = intent.TenantId?.ToString("N"), OutcomeJson = JsonSerializer.Serialize(view), CreatedAt = now };
        return AZOAResult<HoldLocalCommandPlan>.Success(new(result, child, mutation, receipt));
    }

    private static AZOAResult<HoldLocalCommandPlan> Fail(string? message)
        => AZOAResult<HoldLocalCommandPlan>.Failure(message ?? "The hold command cannot be applied.");
}
