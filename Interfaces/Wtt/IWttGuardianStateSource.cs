using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Provider-owned authoritative guardian and foreign-emitter state, never proof verification.</summary>
public interface IWttGuardianStateSource : AZOA.WebAPI.Interfaces.IBlockchainProviderModule
{
    /// <summary>Reads the requested guardian set, current set/expiry and registered emitter at one finalized target block; no configured fallback.</summary>
    Task<AZOAResult<WttGuardianEvidence>> ReadWttGuardianEvidenceAsync(uint guardianSetIndex, ushort emitterChain, string emitterAddress,
        CancellationToken ct = default);
}
