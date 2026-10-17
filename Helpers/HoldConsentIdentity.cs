using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Preserves exact transfer and recipient consent identities across response loss.</summary>
public static class HoldConsentIdentity
{
    public static string Id(string kind, string id, Guid actor, Guid? tenant, Guid requestId)
        => kind == "Transfer"
            ? CanonicalJson.Hash(new { Domain = "hold-continue-v1", id, ActorUserId = actor, TenantId = tenant, RequestId = requestId })
            : CanonicalJson.Hash(new { Domain = "hold-acceptance-consent-v1", Id = id, ActorUserId = actor, TenantId = tenant, RequestId = requestId });

    public static string RequestHash(string kind, string id, Guid actor, Guid? tenant, HoldContinuationRequest request)
        => kind == "Transfer"
            ? CanonicalJson.Hash(new { id, ActorUserId = actor, TenantId = tenant, request })
            : CanonicalJson.Hash(new { Id = id, ActorUserId = actor, TenantId = tenant, request });
}
