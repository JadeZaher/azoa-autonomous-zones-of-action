using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence seam for the wallet-challenge auth nonce
/// (<see cref="WalletAuthChallenge"/>, user-sovereign-identity §1, AC1/AC1b).
///
/// <para>The contract's load-bearing method is <see cref="TryConsumeAsync"/>: a
/// SINGLE atomic conditional UPDATE that consumes a live nonce exactly once. It is
/// the no-TOCTOU single-use primitive — two concurrent verifies of one nonce yield
/// exactly one <c>true</c>. The manager performs the signature check only AFTER a
/// winning consume, so a replay can never re-present a spent nonce.</para>
/// </summary>
public interface IWalletAuthChallengeStore
{
    /// <summary>Persists a freshly-issued challenge (CREATE; UNIQUE on nonce).</summary>
    Task<AZOAResult<WalletAuthChallenge>> CreateAsync(WalletAuthChallenge challenge, CancellationToken ct = default);

    /// <summary>
    /// Loads a challenge by its UNIQUE nonce, or <c>Result == null</c> with no error
    /// when none matches. Used to reconstruct/compare the stored
    /// <see cref="WalletAuthChallenge.DomainMessage"/> on verify.
    /// </summary>
    Task<AZOAResult<WalletAuthChallenge>> GetByNonceAsync(string nonce, CancellationToken ct = default);

    /// <summary>
    /// Resolves the latest still-live (unconsumed, unexpired at <paramref name="now"/>)
    /// challenge for an exact <c>(address, chainType)</c> pair, newest first, or
    /// <c>Result == null</c> with no error. Lets verify proceed from a
    /// signature-only request body (the message embeds the nonce).
    /// </summary>
    Task<AZOAResult<WalletAuthChallenge>> GetLatestLiveByAddressAsync(
        string address, string chainType, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// ATOMIC single-use consume (AC1). A conditional UPDATE that succeeds iff the
    /// nonce is currently unconsumed AND unexpired at <paramref name="now"/>; returns
    /// <c>true</c> iff exactly one row was consumed. This is the no-TOCTOU primitive —
    /// the caller MUST treat a <c>false</c> as "already consumed/expired → reject" and
    /// MUST verify the signature only after a <c>true</c>.
    /// </summary>
    Task<AZOAResult<bool>> TryConsumeAsync(string nonce, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Counts the live (unconsumed, unexpired at <paramref name="now"/>) challenges for
    /// an <c>(address, chainType)</c> pair. Supports the per-address nonce-flood cap
    /// (H1) layered above the per-IP rate limiter.
    /// </summary>
    Task<AZOAResult<int>> CountLiveByAddressAsync(
        string address, string chainType, DateTime now, CancellationToken ct = default);
}
