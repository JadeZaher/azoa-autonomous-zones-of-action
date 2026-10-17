using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Separates immutable hold command replay from network-qualified aggregate identity.</summary>
public static class HoldCommandIdentity
{
    public static string Operation(HoldCommandIntent intent)
        => Operation(intent.ActorUserId, intent.TenantId, intent.RequestKey);

    public static string Operation(Guid actor, Guid? tenant, string requestKey)
        => CanonicalJson.Hash(new { Domain = "hold-command-v1", ActorUserId = actor, TenantId = tenant, RequestKey = requestKey });

    public static string Hold(HoldCommandIntent intent, string? holdId = null)
        => CanonicalJson.Hash(new { Domain = "hold-custody-v1", intent.Asset.ChainType, intent.Asset.Network,
            intent.Asset.ProtocolIdentity, intent.CustodyTenantId, intent.OwnerUserId, HoldId = holdId ?? intent.HoldId });
}
