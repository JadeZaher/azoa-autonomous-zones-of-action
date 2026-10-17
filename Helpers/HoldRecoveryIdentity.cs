using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds recovery replay to one original operation and actual participant request.</summary>
public static class HoldRecoveryIdentity
{
    public static string Id(string kind, string operationId, Guid actor, Guid? tenant, Guid requestId)
        => CanonicalJson.Hash(new { Domain = "hold-recovery-v1", Kind = kind, OperationId = operationId, Actor = actor, Tenant = tenant, RequestId = requestId });

    public static string RequestHash(string kind, string operationId, Guid actor, Guid? tenant, HoldRecoveryRequest request)
        => CanonicalJson.Hash(new { Kind = kind, OperationId = operationId, Actor = actor, Tenant = tenant, Request = request });
}
