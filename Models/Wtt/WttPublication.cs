namespace AZOA.WebAPI.Models.Wtt;

/// <summary>Exact finalized core publication identity; proof and target settlement remain separate.</summary>
public sealed record WttPublication(string ProtocolIdentity, string CoreBridge, string TokenBridge,
    string TransactionId, string BlockIdentity, string EventIndex, uint Timestamp, uint Nonce,
    ushort EmitterChain, string EmitterAddress, string Sequence, byte ConsistencyLevel,
    string PayloadHex, string BodyHex, string BodyDigest);
