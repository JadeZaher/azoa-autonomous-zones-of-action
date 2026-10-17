namespace AZOA.WebAPI.Helpers;

/// <summary>Stable logical Emit identity, independent of delivery attempts and mutable registration state.</summary>
public static class ActionEmitIdentity
{
    public static string Create(Guid runId, Guid nodeId, Guid actorId, Guid? tenantId)
        => CanonicalJson.Hash(new { Domain = "action-emit-v1", RunId = runId, NodeId = nodeId,
            ActorId = actorId, TenantId = tenantId })[..32];
}
