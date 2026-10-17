namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Server-owned finalized-head age limits for exact pinned protocol identities.</summary>
public sealed class WttFinalityOptions
{
    public List<WttFinalizedHeadPolicy> Networks { get; set; } = [];
}

/// <summary>Maximum acceptable finalized snapshot age, including the network's normal finality delay.</summary>
public sealed record WttFinalizedHeadPolicy(string ProtocolIdentity, int MaximumLagSeconds);
