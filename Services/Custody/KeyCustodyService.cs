using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Custody;

// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddScoped<IKeyCustodyService, KeyCustodyService>();
//
// Scoped lifetime matches IWalletStore (Program.cs:284 AddScoped<IWalletStore,…>);
// WalletKeyService (singleton, Program.cs:370) and IConfiguration are both safe to
// capture from a scoped service. Register near the WalletManager registration
// (Program.cs:372 AddScoped<IWalletManager, WalletManager>()).

/// <summary>
/// The single audited choke point for resolving a decrypted signing key
/// (<c>custody-key-management</c> track). Composes the existing primitives —
/// <see cref="IWalletStore"/> for the record and
/// <see cref="WalletKeyService.DecryptPrivateKey"/> for the cleartext — behind the
/// decrypt→sign→zero higher-order contract. Aside from
/// <c>WalletManager.ExportWalletAsync</c>, this is the ONLY type permitted to call
/// <see cref="WalletKeyService.DecryptPrivateKey"/>.
/// <para>
/// Security invariants enforced here: ownership is checked on every per-user
/// resolve (IDOR guard) <b>before</b> any decrypt; only <see cref="WalletType.Platform"/>
/// wallets are signable; the cleartext key exists only as a local <see cref="byte"/>
/// array inside the resolvers and is wiped via
/// <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/> in a <c>finally</c>
/// (even when the <c>sign</c> delegate throws); the resolver returns only the
/// signer's result, never the key; and no key, seed phrase, or decrypted buffer is
/// ever logged.
/// </para>
/// <para>
/// The <b>platform</b> door is per-chain: each chain declares its own config secret and
/// its own derivation rule (<see cref="PlatformKeyDerivationRegistry"/>), and a chain
/// with no registered rule fails closed naming that chain. There is no shared default
/// derivation to fall through to — see <c>Services/Custody/AGENTS.md</c>
/// §platform-key-derivation.
/// </para>
/// </summary>
public sealed class KeyCustodyService : IKeyCustodyService
{
    private readonly IWalletStore _walletStore;
    private readonly WalletKeyService _keyService;
    private readonly IConfiguration _config;
    private readonly ITenantConsentGate _consentGate;
    private readonly PlatformKeyDerivationRegistry _derivations;

    /// <summary>
    /// Reserved sentinel id for the platform pseudo-wallet. It is deliberately NOT a
    /// persisted wallet row: the platform key is config-sourced, per chain
    /// (<see cref="IPlatformKeyDerivation.PlatformKeyConfigPath"/>), which keeps a
    /// value-bearing platform key out of the per-user wallet table. Callers resolve the
    /// platform key via <see cref="WithPlatformSigningKeyAsync{T}(string, bool, SigningContext, Func{byte[], Task{T}})"/>,
    /// not by passing this id to <see cref="WithSigningKeyAsync{T}(Guid, Guid, Func{byte[], Task{T}})"/>.
    /// </summary>
    public static readonly Guid PlatformWalletId = new("00000000-0000-0000-0000-0000000a1f0d");

    /// <summary>
    /// Config path for the <b>Algorand</b> platform signing mnemonic. Aligned with the
    /// interim resolver the signing-core track shipped
    /// (<c>AlgorandProvider.ResolveInterimKeyMaterial</c>, retired deploy registry B2) and
    /// the faucet precedent (<c>Core/AlgorandFaucet.cs:45</c>), so the platform key
    /// supply is single-sourced.
    /// <para>
    /// This is one chain's path, NOT the platform path. Every chain declares its own
    /// (<see cref="IPlatformKeyDerivation.PlatformKeyConfigPath"/>); resolve a chain's
    /// path through <see cref="PlatformKeyDerivationRegistry"/> rather than reusing this
    /// constant for non-Algorand work.
    /// </para>
    /// </summary>
    public const string PlatformMnemonicConfigPath = AlgorandPlatformKeyDerivation.ConfigPath;

    /// <summary>
    /// Chain the chain-LESS platform overloads resolve against. Those overloads predate
    /// per-chain derivation and their only production callers were Algorand paths, so
    /// pinning them keeps historical behaviour byte-identical. It is a pinned legacy
    /// contract, NOT a fallback: a caller that names a chain gets that chain's key or a
    /// fail-closed error, and never lands here. New callers must use the chain-explicit
    /// overload or stamp <see cref="SigningContext.ChainType"/>.
    /// </summary>
    public const string LegacyPlatformChainType = "Algorand";

    public KeyCustodyService(
        IWalletStore walletStore,
        WalletKeyService keyService,
        IConfiguration config,
        ITenantConsentGate consentGate)
        : this(walletStore, keyService, config, consentGate, PlatformKeyDerivationRegistry.Default)
    {
    }

    /// <summary>Derivation-injecting overload; the registry is stateless, so DI normally
    /// selects the shorter constructor and this one exists for tests.</summary>
    public KeyCustodyService(
        IWalletStore walletStore,
        WalletKeyService keyService,
        IConfiguration config,
        ITenantConsentGate consentGate,
        PlatformKeyDerivationRegistry derivations)
    {
        _walletStore = walletStore;
        _keyService = keyService;
        _config = config;
        _consentGate = consentGate ?? throw new ArgumentNullException(nameof(consentGate));
        _derivations = derivations ?? throw new ArgumentNullException(nameof(derivations));
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> WithSigningKeyAsync<T>(SigningContext ctx, Func<byte[], Task<T>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);

        // tenant-consent-delegation C1/AC4: LIVE consent check BEFORE any decrypt.
        // No-op allow for a user-driven context; fail-closed for a tenant-driven one
        // with no covering grant — even though the ownership IDOR check below would
        // pass (wallet.AvatarId == ctx.AvatarId). This is THE single chokepoint.
        var gate = await _consentGate.EnsureAllowedAsync(ctx);
        if (gate.IsError)
            return new AZOAResult<T> { IsError = true, Message = gate.Message };

        return await WithSigningKeyAsync(ctx.WalletId, ctx.AvatarId, sign);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> WithSigningKeyAsync<T>(Guid walletId, Guid avatarId, Func<byte[], Task<T>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        var result = new AZOAResult<T>();

        // 1. Load — mirrors WalletManager.ExportWalletAsync (:307-309).
        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result is null)
        {
            result.IsError = true;
            result.Message = "Wallet not found.";
            return result;
        }

        var wallet = walletResult.Result;

        // 2. IDOR guard — caller-supplied avatar identity is the authority (STARODK
        //    precedent). Returns BEFORE any decrypt (mirror :313-314).
        if (wallet.AvatarId != avatarId)
        {
            result.IsError = true;
            result.Message = "Wallet not owned by this avatar.";
            return result;
        }

        // 3. Type guard — external wallets are never signable (mirror :316-317).
        if (wallet.WalletType != WalletType.Platform)
        {
            result.IsError = true;
            result.Message = "Only platform-managed wallets can be signed with. External wallets sign client-side.";
            return result;
        }

        // 4. Must carry ciphertext (mirror :319-320).
        if (string.IsNullOrEmpty(wallet.EncryptedPrivateKey))
        {
            result.IsError = true;
            result.Message = "No private key stored for this wallet.";
            return result;
        }

        return await DecryptSignZeroAsync(wallet.EncryptedPrivateKey, sign);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
        bool isPlatformContext, SigningContext ctx, Func<byte[], Task<T>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);

        // tenant-consent-delegation C2/AC4b: Grant / FungibleTokenCreate sign with the
        // PLATFORM key yet are tenant-DRIVEN on a user's behalf. Run the LIVE consent
        // check BEFORE any decrypt; a tenant-driven platform op with no covering grant
        // fails closed. Non-tenant-driven ⇒ ordinary platform path.
        var gate = await _consentGate.EnsureAllowedAsync(ctx);
        if (gate.IsError)
            return new AZOAResult<T> { IsError = true, Message = gate.Message };

        return await ResolvePlatformKeyAsync(ChainTypeFor(ctx), isPlatformContext, sign);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(
        string chainType, bool isPlatformContext, SigningContext ctx, Func<byte[], Task<T>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);

        var gate = await _consentGate.EnsureAllowedAsync(ctx);
        if (gate.IsError)
            return new AZOAResult<T> { IsError = true, Message = gate.Message };

        return await ResolvePlatformKeyAsync(chainType, isPlatformContext, sign);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<T>> WithPlatformSigningKeyAsync<T>(bool isPlatformContext, Func<byte[], Task<T>> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        return await ResolvePlatformKeyAsync(LegacyPlatformChainType, isPlatformContext, sign);
    }

    /// <summary>
    /// The chain-aware platform door. Authority → chain → that chain's derivation →
    /// that chain's config secret → derive → sign → zero. Every step that cannot be
    /// satisfied returns an error result; none of them falls back to another chain,
    /// and none of them puts a secret or a key byte in the message.
    /// </summary>
    private async Task<AZOAResult<T>> ResolvePlatformKeyAsync<T>(
        string? chainType, bool isPlatformContext, Func<byte[], Task<T>> sign)
    {
        var result = new AZOAResult<T>();

        // Platform-authority guard (decisions table option (c)): the platform
        // pseudo-wallet has no AvatarId to compare, so authority is the caller's
        // explicit assertion. Only platform-authority managers pass `true`; there is
        // deliberately no controller surface for this path. A non-platform caller
        // returns an error and performs NO decrypt.
        if (!isPlatformContext)
        {
            result.IsError = true;
            result.Message = "Platform signing key is resolvable only from a platform-authority context.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(chainType))
        {
            result.IsError = true;
            result.Message =
                "No chain was named for the platform signing key. Refusing to guess one: a key derived by "
                + "another chain's rule signs for an account the caller never named.";
            return result;
        }

        if (!_derivations.TryGet(chainType, out var derivation))
        {
            // THE fail-closed case. Naming the chain (never a value) is what makes this
            // actionable; falling through to a registered chain's derivation would hand
            // the signer a wrong-shaped or wrong-account key.
            result.IsError = true;
            result.Message =
                $"No platform signing key derivation is registered for chain '{chainType.Trim()}'. "
                + $"Chains with a platform key door: {_derivations.RegisteredChainTypesDisplay}. "
                + "Refusing to derive this chain's key by another chain's rule.";
            return result;
        }

        var configured = _config.GetValue<string>(derivation.PlatformKeyConfigPath);
        if (string.IsNullOrWhiteSpace(configured))
        {
            result.IsError = true;
            result.Message =
                $"No platform signing key configured for chain '{derivation.ChainType}' "
                + $"(set '{derivation.PlatformKeyConfigPath}').";
            return result;
        }

        // Derive the raw key bytes this chain's signer expects — the SAME representation
        // WalletKeyService persists for that chain (Algorand2 ClearTextPrivateKey, Solnet's
        // 64-byte secret, a raw secp256k1 scalar). The derivation owns a buffer this method
        // can zero independently of any SDK-internal keypair, and it is total: a malformed
        // secret yields `false`, never a partial key and never an exception carrying the
        // secret. The signer cannot tell this from a user-key resolve — same byte[]
        // surface, same zeroing contract.
        if (!derivation.TryDerive(configured, out var key))
        {
            result.IsError = true;
            result.Message =
                $"The configured platform signing key for chain '{derivation.ChainType}' could not be derived: "
                + $"'{derivation.PlatformKeyConfigPath}' must be a valid {derivation.SecretDescription}.";
            return result;
        }

        try
        {
            if (key.Length != derivation.KeyLength)
            {
                result.IsError = true;
                result.Message =
                    $"The platform signing key derived for chain '{derivation.ChainType}' is {key.Length} bytes, "
                    + $"not the expected {derivation.KeyLength}. Refusing to sign with a mismatched key.";
                return result;
            }

            result.Result = await sign(key);
            return result;
        }
        catch (Exception ex)
        {
            return result.CaptureException(ex, $"Platform signing failed: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Chain for a context-carrying platform resolve: the stamped chain when the caller
    /// supplied one, else the pinned legacy chain. Never inspects config to guess.
    /// </summary>
    private static string ChainTypeFor(SigningContext ctx) =>
        string.IsNullOrWhiteSpace(ctx.ChainType) ? LegacyPlatformChainType : ctx.ChainType;

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> CanSignAsync(Guid walletId, Guid avatarId)
    {
        // Ownership/eligibility predicate — NO decrypt. Same guards as the resolver,
        // surfaced as a pre-flight so callers can check without touching key bytes.
        var result = new AZOAResult<bool> { Result = false };

        var walletResult = await _walletStore.GetByIdAsync(walletId, default);
        if (walletResult.IsError || walletResult.Result is null)
        {
            result.IsError = true;
            result.Message = "Wallet not found.";
            return result;
        }

        var wallet = walletResult.Result;

        if (wallet.AvatarId != avatarId)
        {
            result.IsError = true;
            result.Message = "Wallet not owned by this avatar.";
            return result;
        }

        if (wallet.WalletType != WalletType.Platform)
        {
            result.IsError = true;
            result.Message = "External wallets are not signable server-side.";
            return result;
        }

        if (string.IsNullOrEmpty(wallet.EncryptedPrivateKey))
        {
            result.IsError = true;
            result.Message = "No private key stored for this wallet.";
            return result;
        }

        result.Result = true;
        result.Message = "Wallet is signable by this avatar.";
        return result;
    }

    /// <inheritdoc />
    public AZOAResult<IWallet> RewrapAsync(IWallet wallet, WalletKeyService oldKeyService, WalletKeyService newKeyService)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        ArgumentNullException.ThrowIfNull(oldKeyService);
        ArgumentNullException.ThrowIfNull(newKeyService);

        var result = new AZOAResult<IWallet>();

        // Per-wallet re-wrap primitive: decrypt under the OLD data-key, re-encrypt
        // under the NEW one, zeroing the transient cleartext buffer. Wired enough to
        // be unit-testable (value encrypted under key A decrypts after rewrap under
        // key B); the operational orchestration (dual-key window, batch, rollback) is
        // the follow-up tracked in conductor/tracks/chain-value-routes — NOT shipped here.
        //
        // CAVEAT (P1 / string-immutability): WalletKeyService.{Decrypt,Encrypt} work
        // in hex strings, which .NET cannot reliably zero. Only the byte[] decoded
        // from the hex is wiped below; the intermediate strings live until GC. A
        // first-class byte[] decrypt/encrypt overload on WalletKeyService is the
        // filed follow-up (Lane A owns WalletKeyService) — see conductor/tracks/chain-value-routes.
        try
        {
            if (!string.IsNullOrEmpty(wallet.EncryptedPrivateKey))
            {
                var pkHex = oldKeyService.DecryptPrivateKey(wallet.EncryptedPrivateKey);
                byte[] pkBytes = ToZeroableBuffer(pkHex);
                try
                {
                    wallet.EncryptedPrivateKey = newKeyService.EncryptPrivateKey(pkHex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkBytes);
                }
            }

            if (!string.IsNullOrEmpty(wallet.EncryptedSeedPhrase))
            {
                var seed = oldKeyService.DecryptSeedPhrase(wallet.EncryptedSeedPhrase);
                byte[] seedBytes = ToZeroableBuffer(seed);
                try
                {
                    wallet.EncryptedSeedPhrase = newKeyService.EncryptSeedPhrase(seed);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seedBytes);
                }
            }

            result.Result = wallet;
            result.Message = "Wallet ciphertext re-wrapped under the new data-key.";
            return result;
        }
        catch (Exception ex)
        {
            return result.CaptureException(ex, $"Re-wrap failed: {ex.Message}");
        }
    }

    // ─── internals ───

    /// <summary>
    /// Decrypt JIT straight into a zeroable <see cref="byte"/> array via
    /// <see cref="WalletKeyService.DecryptPrivateKeyBytes"/>, run
    /// <paramref name="sign"/>, and wipe the bytes in a <c>finally</c> (even on
    /// signer throw). Returns the signer's result wrapped in
    /// <see cref="AZOAResult{T}"/>; NEVER the key.
    /// <para>
    /// final-hardening B5: the cleartext key no longer materializes as an immutable
    /// hex string on this hot path — <c>DecryptPrivateKeyBytes</c> yields the raw
    /// key bytes directly and zeroes its own transient buffers. Only the raw-key
    /// <see cref="byte"/> array reaches here, and it is zeroed below.
    /// </para>
    /// </summary>
    private async Task<AZOAResult<T>> DecryptSignZeroAsync<T>(string encryptedPrivateKey, Func<byte[], Task<T>> sign)
    {
        var result = new AZOAResult<T>();

        byte[] key;
        try
        {
            // Raw key bytes (no hex-string intermediate). Algorand persists the
            // 32-byte Ed25519 seed; Solana the 64-byte secret. The signer
            // reconstructs its chain account from these exact bytes.
            key = _keyService.DecryptPrivateKeyBytes(encryptedPrivateKey);
        }
        catch (Exception ex)
        {
            return result.CaptureException(ex, $"Decryption failed: {ex.Message}");
        }

        try
        {
            result.Result = await sign(key);
            return result;
        }
        catch (Exception ex)
        {
            // Capture the signer's failure as an error result — the finally below
            // still zeroes the key. The exception message must never include key bytes.
            return result.CaptureException(ex, $"Signing failed: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>UTF-8 bytes of a transient secret string, solely so the buffer can be
    /// zeroed after re-encryption (the string itself cannot be — see P1 caveat).</summary>
    private static byte[] ToZeroableBuffer(string value) => System.Text.Encoding.UTF8.GetBytes(value);
}
