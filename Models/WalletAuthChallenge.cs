namespace AZOA.WebAPI.Models;

/// <summary>
/// A single-use wallet-challenge auth nonce (user-sovereign-identity §1, AC1/AC1b).
/// A user proves control of an external wallet by signing the exact
/// <see cref="DomainMessage"/> bytes; the server re-derives and re-validates every
/// field of that message on verify, then atomically consumes the nonce.
///
/// <para>The challenge is bound to a single <c>(address, chainType)</c> pair and a
/// short TTL (≤5 min). It is consumed exactly once at a winning verify — the
/// atomic <c>TryConsumeAsync</c> conditional UPDATE is the no-TOCTOU primitive:
/// two concurrent verifies of one nonce yield exactly one success.</para>
///
/// <para><b>Domain separation (AC1b).</b> <see cref="DomainMessage"/> is NOT a bare
/// nonce — it embeds a fixed domain prefix, the AZOA issuer/audience, the
/// chainType, the address, the nonce, and the expiry. Verify rejects the signature
/// if any of those fields fails to match the server-issued challenge, preventing
/// replay across chains, addresses, AZOA instances, and other apps.</para>
/// </summary>
public class WalletAuthChallenge
{
    /// <summary>Stable record id (Guid; persisted as the SurrealDB record id).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The wallet address the challenge is bound to (chain-native encoding).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The chain type the challenge is bound to (e.g. <c>algorand</c>).</summary>
    public string ChainType { get; set; } = string.Empty;

    /// <summary>Cryptographically-random one-time nonce; UNIQUE across the table.</summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// The EXACT human-readable bytes the user signs (AC1b). Stored verbatim so
    /// verify re-validates the signature against the same bytes it issued — the
    /// server is the single source of truth for the signed message.
    /// </summary>
    public string DomainMessage { get; set; } = string.Empty;

    /// <summary>Expiry instant (UTC). Past this the challenge is dead even if unconsumed.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Single-use marker (UTC). Non-null = already consumed; never reusable.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True iff the challenge is unconsumed AND not yet expired at <paramref name="now"/>.
    /// This is the in-memory liveness predicate; the AUTHORITATIVE single-use
    /// gate is the store's atomic <c>TryConsumeAsync</c> (no-TOCTOU), not this method.
    /// </summary>
    public bool IsLiveAt(DateTime now) => ConsumedAt == null && now < ExpiresAt;
}
