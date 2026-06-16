using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

/// <summary>
/// Custody policy/lifecycle seam around the real signing primitive
/// (<c>custody-key-management</c> track). It is the single audited choke point
/// through which a decrypted signing key is resolved, handed to a signer, and
/// zeroed — never returned, never logged, never persisted.
/// <para>
/// The contract is <b>higher-order</b>: callers pass a <c>sign</c> delegate and
/// receive only its result. The cleartext key material exists solely as a local
/// <see cref="byte"/> array inside the resolver's call stack and is wiped in a
/// <c>finally</c> (even when the delegate throws), so "zero after use" is
/// structurally enforced rather than left to caller discipline.
/// </para>
/// <para>
/// This interface is the swap seam for the KMS/HSM mainnet gate
/// (<c>DEPLOY-STEPS-TODO.md</c> B3): a future <c>KmsKeyCustodyService</c>
/// implements the same interface and the signer is unchanged.
/// </para>
/// </summary>
public interface IKeyCustodyService
{
    /// <summary>
    /// Resolve the per-Avatar signing key for <paramref name="walletId"/>, hand its
    /// decrypted bytes to <paramref name="sign"/>, and zero them before returning.
    /// <para>
    /// Flow (mirrors <c>WalletManager.ExportWalletAsync</c>): load wallet → IDOR
    /// guard (<c>wallet.AvatarId == avatarId</c>, else error <b>before any
    /// decrypt</b>) → type guard (<see cref="WalletType.Platform"/> only) →
    /// non-empty ciphertext → decrypt JIT into a <see cref="byte"/> array →
    /// <c>try { sign(key) } finally { ZeroMemory(key) }</c>.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The signer's result type (e.g. signed-envelope bytes).</typeparam>
    /// <param name="walletId">The wallet whose key signs.</param>
    /// <param name="avatarId">The authenticated caller's avatar; the ownership authority.</param>
    /// <param name="sign">
    /// The signing callback. Receives the decrypted private-key bytes for the
    /// duration of the call only; the array is zeroed once the callback returns
    /// or throws. The callback MUST NOT retain the bytes.
    /// </param>
    /// <returns>The signer's result wrapped in <see cref="OASISResult{T}"/>; NEVER the key.</returns>
    Task<OASISResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid avatarId, Func<byte[], Task<T>> sign);

    /// <summary>
    /// Resolve the <b>platform</b> signing key (a config-sourced pseudo-wallet, not
    /// a per-user record) and run the same decrypt→sign→zero contract.
    /// <para>
    /// The platform pseudo-wallet has no <c>AvatarId</c> to compare, so the
    /// ownership guard is replaced by a <b>caller-asserted authority flag</b>:
    /// <paramref name="isPlatformContext"/> must be <c>true</c>. Only
    /// platform-authority managers (never a controller) may call this with
    /// <c>true</c>; a <c>false</c> assertion returns an error and performs no
    /// decrypt. There is deliberately no controller surface for this method.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The signer's result type.</typeparam>
    /// <param name="isPlatformContext">
    /// The calling manager's assertion that it holds platform/manager authority.
    /// Must be <c>true</c> to resolve the platform key.
    /// </param>
    /// <param name="sign">The signing callback; same lifetime/zeroing contract as
    /// <see cref="WithSigningKeyAsync{T}"/>.</param>
    Task<OASISResult<T>> WithPlatformSigningKeyAsync<T>(bool isPlatformContext, Func<byte[], Task<T>> sign);

    /// <summary>
    /// Ownership/eligibility predicate — <b>no decrypt</b>. Lets callers pre-flight
    /// whether <paramref name="walletId"/> is signable by <paramref name="avatarId"/>
    /// (exists, owned, <see cref="WalletType.Platform"/>, has ciphertext) without
    /// touching key material.
    /// </summary>
    Task<OASISResult<bool>> CanSignAsync(Guid walletId, Guid avatarId);

    /// <summary>
    /// <b>Design + stub.</b> Re-wrap a wallet's encrypted private key (and seed
    /// phrase, if present) from an old data-key to a new one — the in-process
    /// half of an <c>OASIS:WalletEncryptionKey</c> rotation.
    /// <para>
    /// Operation: <c>cleartext = oldKeyService.DecryptPrivateKey(wallet.EncryptedPrivateKey)</c>
    /// → <c>wallet.EncryptedPrivateKey = newKeyService.EncryptPrivateKey(cleartext)</c>
    /// (same for the seed phrase) → zero the transient cleartext buffer. The
    /// returned wallet carries ciphertext re-wrapped under the new key.
    /// </para>
    /// <para>
    /// FOLLOW-UP (not shipped here): the operational orchestration — a dual-key
    /// read window (accept both old and new during cutover), a batch re-wrap of
    /// every stored wallet, and rollback/abort on partial failure — is its own
    /// track (DEPLOY-STEPS-TODO P2). This method is the per-wallet primitive only,
    /// wired enough to be unit-testable (value encrypted under key A decrypts
    /// after re-wrap under key B).
    /// </para>
    /// </summary>
    /// <param name="wallet">The wallet whose ciphertext fields are re-wrapped (mutated in place).</param>
    /// <param name="oldKeyService">A <c>WalletKeyService</c> bound to the OLD data-key (decrypts).</param>
    /// <param name="newKeyService">A <c>WalletKeyService</c> bound to the NEW data-key (re-encrypts).</param>
    /// <returns>The re-wrapped wallet (ciphertext now under the new key); never cleartext.</returns>
    OASISResult<IWallet> RewrapAsync(
        IWallet wallet,
        WalletKeyService oldKeyService,
        WalletKeyService newKeyService);
}
