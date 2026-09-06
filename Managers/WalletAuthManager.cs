// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddScoped<IWalletAuthManager, WalletAuthManager>();
//
// Depends on (all already registered, or registered by the sibling store/verifier
// comment blocks in this track):
//   - IWalletAuthChallengeStore   → SurrealWalletAuthChallengeStore (Scoped)
//   - IWalletAuthClaimTokenStore  → SurrealWalletAuthClaimTokenStore (Scoped)
//   - IUserStore                → SurrealUserStore (Scoped, already wired)
//   - IWalletSignatureVerifier    → Ed25519SignatureVerifier (Singleton)
//   - IConfiguration              → ambient
// Scoped lifetime matches UserManager/TenantManager. Register near them.

using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Wallet-challenge authentication + claim orchestration (user-sovereign-identity §1/§2).
///
/// <para><b>The no-TOCTOU verify pipeline (AC1).</b> Verify resolves the live challenge
/// for <c>(address, chainType)</c>, ATOMICALLY consumes the nonce
/// (<see cref="IWalletAuthChallengeStore.TryConsumeAsync"/>), and only AFTER a winning
/// consume checks the ed25519 signature over the stored domain message. A spent nonce
/// can never be re-presented; a losing concurrent verify gets <c>false</c> from the
/// consume and fails. A bad signature still leaves the nonce consumed — single-use even
/// on failure is safe and prevents grind-on-one-nonce attacks.</para>
///
/// <para><b>Domain separation (AC1b).</b> The signed message is the server-stored
/// <see cref="WalletAuthChallenge.DomainMessage"/> — a fixed prefix
/// (<see cref="DomainPrefix"/>) + issuer/audience + chainType + address + nonce +
/// expiry. A client-supplied echo is accepted ONLY as an exact-equality cross-check;
/// any mismatch rejects. This blocks cross-chain / cross-address / cross-instance /
/// cross-app replay.</para>
///
/// <para><b>Create-or-login ONLY (AC2/AC2b).</b> A never-seen wallet mints a NEW
/// self-owned user (<c>OwnerTenantId == null</c>); a wallet-bound address logs into
/// THAT user. It NEVER matches on email/username/externalUserId, so an
/// unauthenticated verify can never take over an account created another way. Linking a
/// wallet to an existing account is the separate, authenticated
/// <see cref="LinkWalletAsync"/>.</para>
///
/// <para><b>JWT primitive.</b> Delegates to
/// <see cref="AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer"/> — the same shared
/// mint <see cref="AZOA.WebAPI.Managers.UserManager"/> uses — so the verify result
/// carries the identical claim set (including <c>dapp_role</c>/<c>scope</c>) and is
/// indistinguishable from a password login downstream. This used to be a hand-copied
/// mirror of <c>UserManager.GenerateJwt</c> that drifted; see
/// <see cref="GenerateLoginJwt"/>.</para>
/// </summary>
public sealed class WalletAuthManager : IWalletAuthManager
{
    /// <summary>Domain-separation prefix baked into every signed message (AC1b, C4).</summary>
    private const string DomainPrefix = "AZOA-AUTH-v1";

    /// <summary>Challenge TTL ceiling (AC1: ≤5 min).</summary>
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Claim-invite token TTL (short-lived, AC4).</summary>
    private static readonly TimeSpan ClaimTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Per-(address, chainType) live-challenge cap (H1). Bounds nonce-flooding /
    /// storage-exhaustion above the per-IP rate limiter on the controller.
    /// </summary>
    private const int MaxLiveChallengesPerAddress = 5;

    private readonly IWalletAuthChallengeStore _challenges;
    private readonly IWalletAuthClaimTokenStore _claimTokens;
    private readonly IUserStore _userStore;
    private readonly IWalletSignatureVerifier _signatureVerifier;
    private readonly IConsentGrantStore _consentGrants;
    private readonly IConfiguration _config;

    public WalletAuthManager(
        IWalletAuthChallengeStore challenges,
        IWalletAuthClaimTokenStore claimTokens,
        IUserStore userStore,
        IWalletSignatureVerifier signatureVerifier,
        IConsentGrantStore consentGrants,
        IConfiguration config)
    {
        _challenges = challenges ?? throw new ArgumentNullException(nameof(challenges));
        _claimTokens = claimTokens ?? throw new ArgumentNullException(nameof(claimTokens));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _consentGrants = consentGrants ?? throw new ArgumentNullException(nameof(consentGrants));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    // ── AC1/AC1b: challenge issuance ──────────────────────────────────────────

    public async Task<AZOAResult<WalletChallengeResponse>> CreateChallengeAsync(
        string address, string chainType, CancellationToken ct = default)
    {
        var result = new AZOAResult<WalletChallengeResponse>();

        var addr = (address ?? string.Empty).Trim();
        var chain = ChainNames.NormalizeChain(chainType);
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chain))
        {
            result.IsError = true;
            result.Message = "address and chainType are required.";
            return result;
        }

        var now = DateTime.UtcNow;

        // H1: per-address live-challenge cap (the per-IP limiter lives on the
        // controller). A flood of unconsumed nonces for one address is rejected.
        var liveCount = await _challenges.CountLiveByAddressAsync(addr, chain, now, ct);
        if (!liveCount.IsError && liveCount.Result >= MaxLiveChallengesPerAddress)
        {
            result.IsError = true;
            result.Message = "Too many active challenges for this wallet. Try again shortly.";
            return result;
        }

        var nonce = Crypto.GenerateNonce();
        var expiresAt = now.Add(ChallengeLifetime);
        var domainMessage = BuildDomainMessage(chain, addr, nonce, expiresAt);

        var challenge = new WalletAuthChallenge
        {
            Id = Guid.NewGuid(),
            Address = addr,
            ChainType = chain,
            Nonce = nonce,
            DomainMessage = domainMessage,
            ExpiresAt = expiresAt,
            ConsumedAt = null,
            CreatedAt = now,
        };

        var created = await _challenges.CreateAsync(challenge, ct);
        if (created.IsError || created.Result is null)
        {
            result.IsError = true;
            result.Message = created.IsError ? created.Message : "Failed to create challenge.";
            result.Exception = created.Exception;
            return result;
        }

        result.Result = new WalletChallengeResponse
        {
            Address = addr,
            ChainType = chain,
            Nonce = nonce,
            Message = domainMessage,
            ExpiresAt = expiresAt,
        };
        result.Message = "Challenge issued.";
        return result;
    }

    // ── AC1/AC2: verify → create-or-login ─────────────────────────────────────

    public async Task<AZOAResult<WalletAuthTokenResponse>> VerifyAsync(
        string address, string chainType, string signature, string? message, CancellationToken ct = default)
    {
        var result = new AZOAResult<WalletAuthTokenResponse>();

        var proof = await ConsumeAndVerifyAsync(address, chainType, signature, message, ct);
        if (proof.IsError || proof.Result is null)
        {
            result.IsError = true;
            result.Message = proof.Message;
            result.Exception = proof.Exception;
            return result;
        }

        var addr = proof.Result.Address;
        var chain = proof.Result.ChainType;

        // AC2/AC2b: create-or-login ONLY. Resolve the user bound to EXACTLY this
        // wallet — never by email/username/externalUserId.
        var bound = await _userStore.GetByAuthWalletAsync(addr, chain, ct);
        if (bound.IsError)
        {
            result.IsError = true;
            result.Message = bound.Message;
            result.Exception = bound.Exception;
            return result;
        }

        IUser user;
        if (bound.Result is not null)
        {
            if (bound.Result.Id == NodeOperatorIdentity.UserId)
            {
                result.IsError = true;
                result.Message = "Wallet authentication is not available for this identity.";
                return result;
            }
            user = bound.Result;
        }
        else
        {
            // First-ever verify for this wallet → mint a brand-new self-owned user.
            var createdUser = await CreateSelfOwnedWalletUserAsync(addr, chain, ct);
            if (createdUser.IsError || createdUser.Result is null)
            {
                result.IsError = true;
                result.Message = createdUser.IsError ? createdUser.Message : "Failed to create user.";
                result.Exception = createdUser.Exception;
                return result;
            }
            user = createdUser.Result;
        }

        result.Result = new WalletAuthTokenResponse
        {
            Token = GenerateLoginJwt(user),
            UserId = user.Id,
        };
        result.Message = bound.Result is not null ? "Login successful." : "User created and logged in.";
        return result;
    }

    // ── AC2b: authenticated wallet link ───────────────────────────────────────

    public async Task<AZOAResult<bool>> LinkWalletAsync(
        Guid authedUserId, string address, string chainType, string signature, string? message,
        CancellationToken ct = default)
    {
        var result = new AZOAResult<bool>();

        if (authedUserId == NodeOperatorIdentity.UserId)
        {
            result.IsError = true;
            result.Message = "Wallet authentication is not available for this identity.";
            return result;
        }

        var proof = await ConsumeAndVerifyAsync(address, chainType, signature, message, ct);
        if (proof.IsError || proof.Result is null)
        {
            result.IsError = true;
            result.Message = proof.Message;
            result.Exception = proof.Exception;
            return result;
        }

        var addr = proof.Result.Address;
        var chain = proof.Result.ChainType;

        // Reject if this wallet is already bound to a DIFFERENT user (no silent
        // takeover / no duplicate binding).
        var existingBinding = await _userStore.GetByAuthWalletAsync(addr, chain, ct);
        if (existingBinding.IsError)
        {
            result.IsError = true;
            result.Message = existingBinding.Message;
            result.Exception = existingBinding.Exception;
            return result;
        }
        if (existingBinding.Result is not null && existingBinding.Result.Id != authedUserId)
        {
            result.IsError = true;
            result.Message = "This wallet is already linked to another account.";
            return result;
        }

        // Load the authenticated user (id is from the JWT, never the body).
        var loaded = await _userStore.GetByIdAsync(authedUserId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = "Authenticated user not found.";
            result.Exception = loaded.Exception;
            return result;
        }

        var user = loaded.Result;
        user.AuthWalletAddress = addr;
        user.AuthWalletChainType = chain;

        var saved = await _userStore.UpsertAsync(user, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to link wallet.";
            result.Exception = saved.Exception;
            return result;
        }

        result.Result = true;
        result.Message = "Wallet linked.";
        return result;
    }

    // ── AC4: tenant-minted claim invite ───────────────────────────────────────

    public async Task<AZOAResult<ClaimInviteResponse>> CreateClaimInviteAsync(
        Guid tenantId, Guid childUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ClaimInviteResponse>();

        // Assert ownership. A cross-tenant or unowned target is reported as NotFound
        // (404), never Forbidden — the isolation crux (AC5, mirrors TenantManager).
        var loaded = await _userStore.GetByIdAsync(childUserId, ct);
        if (loaded.IsError || loaded.Result is null
            || loaded.Result.OwnerTenantId is null
            || loaded.Result.OwnerTenantId.Value != tenantId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such child user for this tenant.";
            return result;
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(ClaimTokenLifetime);
        var token = new WalletAuthClaimToken
        {
            Id = Guid.NewGuid(),
            Token = Crypto.GenerateNonce(),
            TargetUserId = childUserId,
            TenantId = tenantId,
            ExpiresAt = expiresAt,
            ConsumedAt = null,
            CreatedAt = now,
        };

        var created = await _claimTokens.CreateAsync(token, ct);
        if (created.IsError || created.Result is null)
        {
            result.IsError = true;
            result.Message = created.IsError ? created.Message : "Failed to create claim invite.";
            result.Exception = created.Exception;
            return result;
        }

        result.Result = new ClaimInviteResponse
        {
            ClaimToken = token.Token,
            TargetUserId = childUserId,
            ExpiresAt = expiresAt,
        };
        result.Message = "Claim invite issued.";
        return result;
    }

    // ── AC3/AC3b/AC4: claim a tenant-provisioned user ───────────────────────

    public async Task<AZOAResult<WalletAuthTokenResponse>> ClaimAsync(
        Guid? authedUserId,
        string? claimToken,
        string? newPassword,
        string? address,
        string? chainType,
        string? signature,
        string? message,
        CancellationToken ct = default)
    {
        var result = new AZOAResult<WalletAuthTokenResponse>();
        var now = DateTime.UtcNow;

        // ── 1. Resolve the target user id from a SERVER-TRUSTED principal only ──
        // (a single-use claim token OR the authenticated child-JWT subject). NEVER a
        // body field — the IDOR sever (AC5). A claim-token path also atomically
        // redeems the token so it can be used exactly once (AC4).
        Guid targetUserId;
        if (!string.IsNullOrWhiteSpace(claimToken))
        {
            var tokenRow = await _claimTokens.GetByTokenAsync(claimToken!.Trim(), ct);
            if (tokenRow.IsError || tokenRow.Result is null)
            {
                result.IsError = true;
                result.Message = TenantAuthorizationError.NotFound + "Invalid or expired claim token.";
                result.Exception = tokenRow.Exception;
                return result;
            }

            // Atomic single-use redeem — a losing/expired/replayed token gets false.
            var consumed = await _claimTokens.TryConsumeAsync(claimToken!.Trim(), now, ct);
            if (consumed.IsError || !consumed.Result)
            {
                result.IsError = true;
                result.Message = TenantAuthorizationError.NotFound + "Invalid or expired claim token.";
                result.Exception = consumed.Exception;
                return result;
            }

            targetUserId = tokenRow.Result.TargetUserId;
        }
        else if (authedUserId.HasValue)
        {
            targetUserId = authedUserId.Value;
        }
        else
        {
            result.IsError = true;
            result.Message = "A claim token or an authenticated principal is required.";
            return result;
        }

        if (targetUserId == NodeOperatorIdentity.UserId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such user to claim.";
            return result;
        }

        // ── 2. Load the target. A miss is NotFound (indistinguishable from
        // cross-tenant, the isolation crux). ──
        var loaded = await _userStore.GetByIdAsync(targetUserId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such user to claim.";
            result.Exception = loaded.Exception;
            return result;
        }
        var user = loaded.Result;

        // ── 3. Set the USER-SIDE credential (M1). Exactly one of {password,
        // wallet-challenge} — strictly user-controlled, never derivable from a tenant
        // child JWT. ──
        var hasPassword = !string.IsNullOrWhiteSpace(newPassword);
        var hasWallet = !string.IsNullOrWhiteSpace(address)
                        || !string.IsNullOrWhiteSpace(chainType)
                        || !string.IsNullOrWhiteSpace(signature);

        if (hasPassword == hasWallet)
        {
            result.IsError = true;
            result.Message = "Provide exactly one user-side credential: a new password OR a wallet-challenge signature.";
            return result;
        }

        if (hasPassword)
        {
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        }
        else
        {
            // Wallet credential: prove control of the wallet via the standard
            // challenge+consume+verify, then bind it (rejecting a wallet already bound
            // elsewhere).
            var proof = await ConsumeAndVerifyAsync(address ?? string.Empty, chainType ?? string.Empty, signature ?? string.Empty, message, ct);
            if (proof.IsError || proof.Result is null)
            {
                result.IsError = true;
                result.Message = proof.Message;
                result.Exception = proof.Exception;
                return result;
            }

            var addr = proof.Result.Address;
            var chain = proof.Result.ChainType;

            var existingBinding = await _userStore.GetByAuthWalletAsync(addr, chain, ct);
            if (existingBinding.IsError)
            {
                result.IsError = true;
                result.Message = existingBinding.Message;
                result.Exception = existingBinding.Exception;
                return result;
            }
            if (existingBinding.Result is not null && existingBinding.Result.Id != user.Id)
            {
                result.IsError = true;
                result.Message = "This wallet is already linked to another account.";
                return result;
            }

            user.AuthWalletAddress = addr;
            user.AuthWalletChainType = chain;
        }

        // ── 4. Sever tenant custody + cut the residual child-JWT window (AC3/AC3b). ──
        user.OwnerTenantId = null;          // IDOR-safe sever (AC3)
        user.AuthNotBefore = now;           // H2/AC3b: reject any pre-claim tenant token
        user.IsActive = true;

        var saved = await _userStore.UpsertAsync(user, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to claim user.";
            result.Exception = saved.Exception;
            return result;
        }

        // ── 4b. Revoke ALL outstanding consent grants this user made (AC3b, security
        // review). The AuthNotBefore watermark is the primary cut (the JWT validator
        // rejects any pre-claim token), but a still-live grant + a stale child JWT is
        // belt-and-suspenders risk: revoking grants makes the signing seam fail closed
        // for ANY residual tenant credential, independent of the watermark check. This
        // is the robust half of the defense-in-depth fix. A revoke failure does NOT
        // fail the claim — the watermark already cut the window — but it IS surfaced.
        var revoked = await _consentGrants.RevokeAllByGrantorAsync(saved.Result.Id, now, ct);

        result.Result = new WalletAuthTokenResponse
        {
            Token = GenerateLoginJwt(saved.Result),
            UserId = saved.Result.Id,
        };
        result.Message = revoked.IsError
            ? "User claimed (warning: outstanding consent grants could not be auto-revoked; they remain subject to the post-claim watermark cut)."
            : "User claimed.";
        return result;
    }

    // ── Shared verify primitive ───────────────────────────────────────────────

    /// <summary>
    /// The no-TOCTOU proof step shared by verify / link / wallet-claim. Resolves the
    /// live challenge for <c>(address, chainType)</c>, re-validates the domain message
    /// (and any client echo), ATOMICALLY consumes the nonce, then verifies the ed25519
    /// signature over the STORED domain message. Returns the resolved challenge on
    /// success. Any failure (no live challenge, field mismatch, lost/expired consume,
    /// bad signature) is an error — and a consumed-but-bad-signature nonce stays
    /// consumed (single-use even on failure).
    /// </summary>
    private async Task<AZOAResult<WalletAuthChallenge>> ConsumeAndVerifyAsync(
        string address, string chainType, string signature, string? clientMessage, CancellationToken ct)
    {
        var result = new AZOAResult<WalletAuthChallenge>();

        var addr = (address ?? string.Empty).Trim();
        var chain = ChainNames.NormalizeChain(chainType);
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chain) || string.IsNullOrWhiteSpace(signature))
        {
            result.IsError = true;
            result.Message = "address, chainType and signature are required.";
            return result;
        }

        var now = DateTime.UtcNow;

        // Resolve the latest live challenge for this exact (address, chainType).
        var lookup = await _challenges.GetLatestLiveByAddressAsync(addr, chain, now, ct);
        if (lookup.IsError)
        {
            result.IsError = true;
            result.Message = lookup.Message;
            result.Exception = lookup.Exception;
            return result;
        }
        var challenge = lookup.Result;
        if (challenge is null)
        {
            result.IsError = true;
            result.Message = "No active challenge for this wallet. Request a new challenge.";
            return result;
        }

        // AC1b: re-validate the embedded fields server-side against the EXACT bytes
        // we will verify the signature over. The stored DomainMessage is the
        // authoritative signed payload (built once at issuance and never reconstructed
        // from round-tripped timestamps, so there is no precision-drift hazard). We
        // confirm the request's (address, chainType) matches the challenge's, and that
        // the stored message actually embeds the bound prefix/chain/address/nonce — so
        // a tampered row can never be honoured.
        var boundChain = string.Equals(challenge.ChainType, chain, StringComparison.Ordinal);
        var boundAddress = string.Equals(challenge.Address, addr, StringComparison.Ordinal);
        var msg = challenge.DomainMessage;
        var wellFormed = msg.StartsWith(DomainPrefix + "\n", StringComparison.Ordinal)
                         && msg.Contains("chain:" + challenge.ChainType + "\n", StringComparison.Ordinal)
                         && msg.Contains("address:" + challenge.Address + "\n", StringComparison.Ordinal)
                         && msg.Contains("nonce:" + challenge.Nonce + "\n", StringComparison.Ordinal);
        if (!boundChain || !boundAddress || !wellFormed)
        {
            result.IsError = true;
            result.Message = "Challenge validation failed.";
            return result;
        }
        // A client echo of the signed bytes must match the stored message EXACTLY
        // (byte-for-byte) — any divergence in any field rejects the verify (AC1b).
        if (clientMessage is not null && !string.Equals(clientMessage, challenge.DomainMessage, StringComparison.Ordinal))
        {
            result.IsError = true;
            result.Message = "Signed message does not match the issued challenge.";
            return result;
        }

        // ATOMIC single-use consume (no-TOCTOU). A lost/expired race gets false → fail.
        var consumed = await _challenges.TryConsumeAsync(challenge.Nonce, now, ct);
        if (consumed.IsError || !consumed.Result)
        {
            result.IsError = true;
            result.Message = "Challenge already used or expired. Request a new challenge.";
            result.Exception = consumed.Exception;
            return result;
        }

        // Decode the signature (accept base64 or hex) and verify over the STORED bytes.
        if (!AZOA.WebAPI.Helpers.Encoding.TryDecodeSignature(signature, out var signatureBytes))
        {
            result.IsError = true;
            result.Message = "Signature is not valid base64 or hex.";
            return result;
        }

        var messageBytes = System.Text.Encoding.UTF8.GetBytes(challenge.DomainMessage);

        bool ok;
        try
        {
            ok = _signatureVerifier.Verify(chain, addr, messageBytes, signatureBytes);
        }
        catch (NotSupportedException ex)
        {
            result.IsError = true;
            result.Message = $"Wallet auth is not supported for chain '{chain}'.";
            result.Exception = ex;
            return result;
        }

        if (!ok)
        {
            // The nonce is already consumed — single-use even on a bad signature.
            result.IsError = true;
            result.Message = "Signature verification failed.";
            return result;
        }

        result.Result = challenge;
        result.Message = "Verified.";
        return result;
    }

    // ── User creation ───────────────────────────────────────────────────────

    /// <summary>
    /// Mints a brand-new self-owned user bound to the wallet (AC2). Synthesizes a
    /// unique username/email from the chain+address (NEVER matched on login) and a
    /// random non-usable password hash — there is no password login path for a
    /// wallet-only user. <c>OwnerTenantId == null</c> by construction.
    /// </summary>
    private async Task<AZOAResult<IUser>> CreateSelfOwnedWalletUserAsync(
        string address, string chainType, CancellationToken ct)
    {
        // A short, unique-ish suffix keeps the synthesized handle readable while the
        // full address guarantees global uniqueness.
        var prefix = address.Length <= 12 ? address : address[..12];
        var unique = Guid.NewGuid().ToString("N")[..8];
        var handle = $"wallet-{chainType}-{prefix}-{unique}".ToLowerInvariant();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = handle,
            Email = $"{handle}@wallet.azoa.local",
            // No password login for a wallet-only user; random hash keeps the column
            // non-empty without granting a usable password.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            IsVerified = false,
            // Self-owned by construction — the durable state for all users (AC2/§3).
            OwnerTenantId = null,
            AuthWalletAddress = address,
            AuthWalletChainType = chainType,
        };

        return await _userStore.UpsertAsync(user, ct);
    }

    // ── Domain message + crypto helpers ───────────────────────────────────────

    /// <summary>
    /// Builds the EXACT domain-separated message bytes-to-sign (AC1b, C4). Deterministic
    /// and human-readable: a fixed prefix, the AZOA issuer/audience (cross-instance /
    /// cross-app separation), the chainType, the address, the nonce, and the ISO-8601
    /// expiry. Every field is re-validated on verify.
    /// </summary>
    private string BuildDomainMessage(string chainType, string address, string nonce, DateTime expiresAtUtc)
    {
        var issuer = _config["Jwt:Issuer"] ?? string.Empty;
        var audience = _config["Jwt:Audience"] ?? string.Empty;
        var expiryIso = expiresAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        // One field per line, label-prefixed, so the signer can read what they sign and
        // verify reconstructs it byte-for-byte.
        var sb = new StringBuilder();
        sb.Append(DomainPrefix).Append('\n');
        sb.Append("issuer:").Append(issuer).Append('\n');
        sb.Append("audience:").Append(audience).Append('\n');
        sb.Append("chain:").Append(chainType).Append('\n');
        sb.Append("address:").Append(address).Append('\n');
        sb.Append("nonce:").Append(nonce).Append('\n');
        sb.Append("expires:").Append(expiryIso);
        return sb.ToString();
    }

    // ── Login JWT — delegates to the single shared issuer ─────────────────────

    /// <summary>Mints the wallet path's login token via the one shared issuer.</summary>
    /// <remarks>
    /// This used to be a hand-copied mirror of <c>UserManager.GenerateJwt</c> that
    /// had silently drifted: it never stamped the dApp-role/scope claims, so a
    /// wallet-authenticated user's token carried no <c>dapp_role</c> and no
    /// <c>scope</c> claims while a password login for the SAME user got both — a
    /// deny-by-default under-privilege (every claim-gated policy here reads absence as
    /// "no authority", never as "allow"), not a fail-open. Delegating to
    /// <see cref="AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer"/> (the same
    /// primitive <see cref="AZOA.WebAPI.Managers.UserManager"/> now uses) makes claim
    /// parity structural instead of hoped-for: there is exactly one JWT-minting
    /// implementation for every interactive login path, so this class of drift can no
    /// longer recur.
    /// </remarks>
    private string GenerateLoginJwt(IUser user)
        => AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer.BuildLoginToken(_config, user);
}
