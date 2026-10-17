namespace AZOA.WebAPI.Helpers;

/// <summary>Domain-separated WTT request, offer and stage identities; see Services/Wormhole/AGENTS.md §Operation journal.</summary>
public static class WttOperationIdentity
{
    public static string RequestKeyHash(string requestKey)
        => CanonicalJson.Hash(new { Domain = "wtt-request-key/1", RequestKey = requestKey });

    public static string Operation(Guid actor, Guid? tenant, string requestKeyHash)
        => CanonicalJson.Hash(new { Domain = "wtt/v1", Actor = actor, Tenant = tenant, RequestKeyHash = requestKeyHash });

    public static string Offer(string intentHash, string sourceQuoteHash, string targetInspectionHash, string? returnOf, DateTimeOffset expiresAt)
        => CanonicalJson.Hash(new { Domain = "wtt-offer/1", intentHash, sourceQuoteHash, targetInspectionHash, returnOf, expiresAt });

    public static string Stage(string operationId, string phase, string stage, int generation)
        => CanonicalJson.Hash(new { Domain = "wtt-stage/v1", operationId, phase, stage, generation });

    public static bool IsCanonicalId(string? value)
        => value is { Length: 64 } && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    public static bool ValidRequestKey(string? key)
        => key is { Length: > 0 and <= 200 } && key == key.Trim() && !key.Any(char.IsControl);
}

/// <summary>Journal stage states after which no further effect from that attempt is possible.</summary>
public static class WttStageStates
{
    public static bool IsTerminal(string state) => state is "Confirmed" or "FailedOnChain" or "ExpiredNoEffect" or "Cancelled" or "Retired";
}
