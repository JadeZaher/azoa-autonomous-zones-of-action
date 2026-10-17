namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Parsed protocol evidence, not a verification or settlement verdict.</summary>
public sealed record WttVaa(
    uint GuardianSetIndex,
    IReadOnlyList<WttGuardianSignature> Signatures,
    uint Timestamp,
    uint Nonce,
    ushort EmitterChain,
    string EmitterAddress,
    string Sequence,
    byte ConsistencyLevel,
    string PayloadHex,
    string BodyHex,
    string BodyDigest,
    string EnvelopeHex);

public sealed record WttGuardianSignature(byte GuardianIndex, string R, string S, byte RecoveryId);

/// <summary>WTT payload 1; amounts are unsigned decimal strings in normalized protocol units.</summary>
public sealed record WttTransferPayload(
    string Amount, string TokenAddress, ushort TokenChain,
    string Recipient, ushort RecipientChain, string Fee);

/// <summary>WTT payload 2; fixed metadata bytes remain lossless until chain-specific interpretation.</summary>
public sealed record WttAttestationPayload(
    string TokenAddress, ushort TokenChain, byte Decimals, string SymbolHex, string NameHex);

public sealed record WttNormalizedAmount(
    string Requested, string Debited, string Dust, string Amount,
    string Fee, string RecipientAmount);
