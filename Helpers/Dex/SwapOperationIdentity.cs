using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Helpers.Dex;

/// <summary>Domain-separated immutable request and stage identities.</summary>
public static class SwapOperationIdentity
{
    public static string Operation(Guid actor, Guid? tenant, string requestKeyHash)
        => CanonicalJson.Hash(new { Domain = "swap/v1", Actor = actor, Tenant = tenant, RequestKeyHash = requestKeyHash });

    public static string RequestKey(string requestKey)
        => CanonicalJson.Hash(new { Domain = "swap-request-key/1", RequestKey = requestKey });

    public static string Preparation(string operationId, string stage, int generation, string intentHash)
        => $"swap:{operationId}:{stage}:{generation}:{intentHash}";

    public static string CleanupPreparation(string operationId, string stage, int generation, string intentHash,
        SwapCleanupConsent consent)
        => $"swap-cleanup:{operationId}:{stage}:{generation}:{intentHash}:{CanonicalJson.Hash(consent)}";

    public static string Stage(string preparationKey)
        => CanonicalJson.Hash(new { Domain = "swap-stage/v1", PreparationKey = preparationKey });
}
