using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds original actor consent to one exact parked opening disclosure.</summary>
public static class HoldWorkflowOpeningIdentity
{
    public static string AuditId(string id, Guid actor, Guid? tenant, Guid requestId)
        => CanonicalJson.Hash(new { Domain = "hold-workflow-opening-accept-v1", Id = id,
            ActorUserId = actor, TenantId = tenant, RequestId = requestId });

    public static string RequestHash(string id, string intentHash, HoldWorkflowOpeningAcceptance acceptance)
        => CanonicalJson.Hash(new { Id = id, IntentHash = intentHash, acceptance });
}
