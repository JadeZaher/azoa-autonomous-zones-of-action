using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Hold;

/// <summary>Projects an immutable admitted liability without granting custody authority.</summary>
internal static class HoldAggregateProjection
{
    public static HoldLedger Create(HoldCommandIntent intent, string holdId, HoldDefinition definition,
        string openingCommandId, DateTimeOffset now, string state = "Held")
    {
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(definition));
        return new HoldLedger { Id = HoldCommandIdentity.Hold(intent, holdId), HoldId = holdId,
            Chain = intent.Asset.ChainType, Network = intent.Asset.Network.ToString(),
            ProtocolIdentity = intent.Asset.ProtocolIdentity, OwnerUserId = intent.OwnerUserId.ToString("N"),
            TenantId = intent.CustodyTenantId is { } tenant ? SurrealLink.ToLink("user", tenant.ToString("N")) : null,
            HoldingAddress = intent.Asset.HoldingAddress, AssetId = intent.Asset.AssetId,
            Amount = definition.QuantityBaseUnits, FundingSource = definition.FundingSource,
            ReservePartition = definition.ReservePartition ?? string.Empty,
            BeneficiaryUserId = SurrealLink.ToLink("user", definition.BeneficiaryUserId.ToString("N"))!,
            FunderUserId = definition.FunderUserId is { } funder ? SurrealLink.ToLink("user", funder.ToString("N")) : null,
            ReleaseNotBefore = definition.ReleaseNotBefore, AutoRelease = definition.AutoRelease,
            Purpose = definition.Purpose, DefinitionJson = json, DefinitionHash = CanonicalJson.HashCanonical(json),
            OpeningCommandId = openingCommandId, State = state, CreatedAt = now, UpdatedAt = now };
    }
}
