namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Finalized target-core state and target token-bridge emitter registration from one pinned observation.</summary>
public sealed record WttGuardianEvidence(string ProtocolIdentity, string CoreBridge, string TokenBridge,
    uint GuardianSetIndex, uint CurrentGuardianSetIndex, IReadOnlyList<string> GuardianAddresses,
    uint ExpirationUnixSeconds, uint ChainTimeUnixSeconds, string BlockIdentity, DateTimeOffset ObservedAt,
    ushort RegisteredEmitterChain, string RegisteredEmitterAddress);

/// <summary>Frozen target deployment and exact finalized source publication to which a VAA must bind.</summary>
public sealed record WttProofExpectation(string TargetProtocolIdentity, string TargetCoreBridge,
    string TargetTokenBridge, string SourceBodyHex);

/// <summary>Cryptographically verified source proof; target redemption is still a separate chain verdict.</summary>
public sealed record WttVerifiedProof(WttVaa Vaa, WttGuardianEvidence GuardianEvidence);
