using AZOA.WebAPI.Core.Blockchain.Wormhole;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Recovers one guardian signature against an independently resolved authoritative 20-byte address.</summary>
public interface IWttGuardianSignatureVerifier
{
    /// <summary>Verifies the exact already-hashed 32-byte digest; no configured guardian fallback or quorum decision.</summary>
    bool VerifyGuardianSignature(byte[] digest, WormholeVaaSignature signature, byte[] expectedGuardianAddress);
}
