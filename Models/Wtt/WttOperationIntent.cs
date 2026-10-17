using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Provider-inspected immutable deployment identity; values are never inferred from a chain family alone.</summary>
public sealed record WttNetworkDescriptor(string ChainType, ChainNetwork Network, string ProtocolIdentity,
    string Environment, ushort ProtocolChain, string CoreBridge, string TokenBridge, string CodeIdentity,
    WttDeploymentIdentity DeploymentIdentity, string EmitterAddressHex);

/// <summary>Provider-converted canonical bytes for one physical WTT deployment, independent of display address encodings.</summary>
public sealed record WttDeploymentIdentity(ushort ProtocolChain, string GenesisHex, string CoreBridgeHex, string TokenBridgeHex);

/// <summary>Current representation and original token identity inspected on the selected source chain.</summary>
public sealed record WttAssetDescriptor(string TokenAddress, bool NativeAsset, byte Decimals,
    ushort OriginChain, string OriginAddress, byte? OriginDecimals, string SymbolHex, string NameHex,
    string BehaviorIdentity);

/// <summary>One consent-bound signing participant; providers independently validate address/key correspondence.</summary>
public sealed record WttSigningAuthority(string Address, SigningContext Context);

/// <summary>Exact per-chain maximum costs; fees, locked deposits and value are never combined across currencies.</summary>
public sealed record WttChainBudget(string MaximumNetworkFee, string MaximumLockedDeposit, string MaximumValue);

/// <summary>Frozen transfer economics and both provider-inspected deployments, independent of a VAA signature envelope.</summary>
public sealed record WttOperationIntent(int Version, string OperationId, Guid ActorUserId, Guid? TenantId,
    WttNetworkDescriptor Source, WttNetworkDescriptor Target, WttAssetDescriptor Asset,
    WttSigningAuthority SourceAuthority, WttSigningAuthority TargetAuthority,
    string RecipientAddress, string RecipientProtocolAddress, string? TargetAssetAddress, byte TargetDecimals,
    WttNormalizedAmount Amount, uint Nonce, byte ConsistencyLevel, bool UnwrapNative, bool RequiresAttestation,
    WttChainBudget SourceBudget, WttChainBudget TargetBudget);

/// <summary>One provider-planned stage and its maximum reservation, never proof of an actual debit.</summary>
public sealed record WttStageReservation(string Stage, string NetworkFee, string LockedDeposit, string Value,
    string? ParticipantAddress = null);

/// <summary>Immutable provider plan admitted before signing; quote identity and complete intent hash are server-owned.</summary>
public sealed record WttStageQuote(string QuoteId, string IntentHash, string ProtocolIdentity, string Phase,
    IReadOnlyList<WttStageReservation> Stages, string PlanJson, DateTimeOffset ExpiresAt);

/// <summary>Exact signed wire and immutable recovery metadata; first-send authority remains in the WTT journal.</summary>
public sealed record WttStagePreparation(string Stage, int Generation, string TransactionId,
    string SignedTransactionBase64, string ProtocolIdentity, string RecoveryMetadata,
    WttStageReservation Reservation, string? AdditionalSignedTransactionsJson = null);

/// <summary>Observed stage evidence; only finalized protocol-specific settlement can complete a transfer.</summary>
public sealed record WttStageObservation(string Verdict, string TransactionId, string ProtocolIdentity,
    string? EvidenceJson = null, WttPublication? Publication = null, string? NetworkFeePaid = null,
    string? Detail = null);

public sealed record WttStageHistory(WttStagePreparation Prepared, WttStageObservation Observation);

/// <summary>Registration requires its own verified metadata attestation; a transfer VAA cannot create a wrapped asset.</summary>
public sealed record WttTargetProofs(WttVerifiedProof Transfer, WttVerifiedProof? Attestation);

/// <summary>Absent wrapped representation is an explicit prerequisite, not a synthetic deployed token address.</summary>
public sealed record WttTargetInspection(WttNetworkDescriptor Network, string? AssetAddress, byte Decimals,
    bool Registered, bool RecipientReady, string RecipientProtocolAddress);

/// <summary>Persisted exact participant message to be signed by that wallet; never an authority to sign as the participant.</summary>
public sealed record WttParticipantPreparation(string OperationId, string Stage, int Generation, string ParticipantAddress,
    string ProtocolIdentity, string UnsignedTransactionBase64, string RecoveryMetadata, WttStageReservation Reservation);
