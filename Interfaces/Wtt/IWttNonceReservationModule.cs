using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Durable-nonce chains expose the account reservation behind a WTT stage key; see `Services/Wormhole/AGENTS.md` §Nonce reservations.</summary>
public interface IWttNonceReservationModule : IBlockchainProviderModule
{
    /// <summary>Reads the deterministic preparation row for one stage generation; any uncertainty reads as <see cref="WttNonceHold.Held"/>.</summary>
    Task<WttNonceHold> ReadWttNonceHoldAsync(WttOperationIntent intent, string phase, string stage, int generation,
        CancellationToken ct = default);

    /// <summary>Releases that row only while it is reserved, unsigned and never disclosed, so the allocator may refill its nonce.</summary>
    Task<bool> ReleaseUnsignedWttReservationAsync(WttOperationIntent intent, string phase, string stage, int generation,
        CancellationToken ct = default);
}
