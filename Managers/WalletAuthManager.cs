// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddScoped<IWalletAuthManager, WalletAuthManager>();
//
// Depends on (all already registered, or registered by the sibling store/verifier
// comment blocks in this track):
//   - IWalletAuthChallengeStore   → SurrealWalletAuthChallengeStore (Scoped)
//   - IWalletAuthClaimTokenStore  → SurrealWalletAuthClaimTokenStore (Scoped)
//   - IAvatarStore                → SurrealAvatarStore (Scoped, already wired)
//   - IWalletSignatureVerifier    → Ed25519SignatureVerifier (Singleton)
//   - IConfiguration              → ambient
// Scoped lifetime matches AvatarManager/TenantManager. Register near them.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

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
/// self-owned avatar (<c>OwnerTenantId == null</c>); a wallet-bound address logs into
/// THAT avatar. It NEVER matches on email/username/externalUserId, so an
/// unauthenticated verify can never take over an account created another way. Linking a
/// wallet to an existing account is the separate, authenticated
/// <see cref="LinkWalletAsync"/>.</para>
///
/// <para><b>JWT primitive.</b> Mirrors <c>AvatarManager.GenerateJwt</c> exactly (same
/// <c>Jwt:Key</c> / HmacSha256 / issuer / audience and login claim shape) — the verify
/// result is indistinguishable from a password login downstream.</para>
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
    private readonly IAvatarStore _avatarStore;
    private readonly IWalletSignatureVerifier _signatureVerifier;
    private readonly IConsentGrantStore _consentGrants;
    private readonly IConfiguration _config;

    public WalletAuthManager(
        IWalletAuthChallengeStore challenges,
        IWalletAuthClaimTokenStore claimTokens,
        IAvatarStore avatarStore,
        IWalletSignatureVerifier signatureVerifier,
        IConsentGrantStore consentGrants,
        IConfiguration config)
    {
        _challenges = challenges ?? throw new ArgumentNullException(nameof(challenges));
        _claimTokens = claimTokens ?? throw new ArgumentNullException(nameof(claimTokens));
        _avatarStore = avatarStore ?? throw new ArgumentNullException(nameof(avatarStore));
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

        // AC2/AC2b: create-or-login ONLY. Resolve the avatar bound to EXACTLY this
        // wallet — never by email/username/externalUserId.
        var bound = await _avatarStore.GetByAuthWalletAsync(addr, chain, ct);
        if (bound.IsError)
        {
            result.IsError = true;
            result.Message = bound.Message;
            result.Exception = bound.Exception;
            return result;
        }

        IAvatar avatar;
        if (bound.Result is not null)
        {
            avatar = bound.Result;
        }
        else
        {
            // First-ever verify for this wallet → mint a brand-new self-owned avatar.
            var createdAvatar = await CreateSelfOwnedWalletAvatarAsync(addr, chain, ct);
            if (createdAvatar.IsError || createdAvatar.Result is null)
            {
                result.IsError = true;
                result.Message = createdAvatar.IsError ? createdAvatar.Message : "Failed to create avatar.";
                result.Exception = createdAvatar.Exception;
                return result;
            }
            avatar = createdAvatar.Result;
        }

        result.Result = new WalletAuthTokenResponse
        {
            Token = GenerateLoginJwt(avatar),
            AvatarId = avatar.Id,
        };
        result.Message = bound.Result is not null ? "Login successful." : "Avatar created and logged in.";
        return result;
    }

    // ── AC2b: authenticated wallet link ───────────────────────────────────────

    public async Task<AZOAResult<bool>> LinkWalletAsync(
        Guid authedAvatarId, string address, string chainType, string signature, string? message,
        CancellationToken ct = default)
    {
        var result = new AZOAResult<bool>();

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

        // Reject if this wallet is already bound to a DIFFERENT avatar (no silent
        // takeover / no duplicate binding).
        var existingBinding = await _avatarStore.GetByAuthWalletAsync(addr, chain, ct);
        if (existingBinding.IsError)
        {
            result.IsError = true;
            result.Message = existingBinding.Message;
            result.Exception = existingBinding.Exception;
            return result;
        }
        if (existingBinding.Result is not null && existingBinding.Result.Id != authedAvatarId)
        {
            result.IsError = true;
            result.Message = "This wallet is already linked to another account.";
            return result;
        }

        // Load the authenticated avatar (id is from the JWT, never the body).
        var loaded = await _avatarStore.GetByIdAsync(authedAvatarId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = "Authenticated avatar not found.";
            result.Exception = loaded.Exception;
            return result;
        }

        var avatar = loaded.Result;
        avatar.AuthWalletAddress = addr;
        avatar.AuthWalletChainType = chain;

        var saved = await _avatarStore.UpsertAsync(avatar, ct);
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
        Guid tenantId, Guid childAvatarId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ClaimInviteResponse>();

        // Assert ownership. A cross-tenant or unowned target is reported as NotFound
        // (404), never Forbidden — the isolation crux (AC5, mirrors TenantManager).
        var loaded = await _avatarStore.GetByIdAsync(childAvatarId, ct);
        if (loaded.IsError || loaded.Result is null
            || loaded.Result.OwnerTenantId is null
            || loaded.Result.OwnerTenantId.Value != tenantId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such child avatar for this tenant.";
            return result;
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(ClaimTokenLifetime);
        var token = new WalletAuthClaimToken
        {
            Id = Guid.NewGuid(),
            Token = Crypto.GenerateNonce(),
            TargetAvatarId = childAvatarId,
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
            TargetAvatarId = childAvatarId,
            ExpiresAt = expiresAt,
        };
        result.Message = "Claim invite issued.";
        return result;
    }

    // ── AC3/AC3b/AC4: claim a tenant-provisioned avatar ───────────────────────

    public async Task<AZOAResult<WalletAuthTokenResponse>> ClaimAsync(
        Guid? authedAvatarId,
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

        // ── 1. Resolve the target avatar id from a SERVER-TRUSTED principal only ──
        // (a single-use claim token OR the authenticated child-JWT subject). NEVER a
        // body field — the IDOR sever (AC5). A claim-token path also atomically
        // redeems the token so it can be used exactly once (AC4).
        Guid targetAvatarId;
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

            targetAvatarId = tokenRow.Result.TargetAvatarId;
        }
        else if (authedAvatarId.HasValue)
        {
            targetAvatarId = authedAvatarId.Value;
        }
        else
        {
            result.IsError = true;
            result.Message = "A claim token or an authenticated principal is required.";
            return result;
        }

        // ── 2. Load the target. A miss is NotFound (indistinguishable from
        // cross-tenant, the isolation crux). ──
        var loaded = await _avatarStore.GetByIdAsync(targetAvatarId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such avatar to claim.";
            result.Exception = loaded.Exception;
            return result;
        }
        var avatar = loaded.Result;

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
            avatar.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
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

            var existingBinding = await _avatarStore.GetByAuthWalletAsync(addr, chain, ct);
            if (existingBinding.IsError)
            {
                result.IsError = true;
                result.Message = existingBinding.Message;
                result.Exception = existingBinding.Exception;
                return result;
            }
            if (existingBinding.Result is not null && existingBinding.Result.Id != avatar.Id)
            {
                result.IsError = true;
                result.Message = "This wallet is already linked to another account.";
                return result;
            }

            avatar.AuthWalletAddress = addr;
            avatar.AuthWalletChainType = chain;
        }

        // ── 4. Sever tenant custody + cut the residual child-JWT window (AC3/AC3b). ──
        avatar.OwnerTenantId = null;          // IDOR-safe sever (AC3)
        avatar.AuthNotBefore = now;           // H2/AC3b: reject any pre-claim tenant token
        avatar.IsActive = true;

        var saved = await _avatarStore.UpsertAsync(avatar, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to claim avatar.";
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
            AvatarId = saved.Result.Id,
        };
        result.Message = revoked.IsError
            ? "Avatar claimed (warning: outstanding consent grants could not be auto-revoked; they remain subject to the post-claim watermark cut)."
            : "Avatar claimed.";
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

    // ── Avatar creation ───────────────────────────────────────────────────────

    /// <summary>
    /// Mints a brand-new self-owned avatar bound to the wallet (AC2). Synthesizes a
    /// unique username/email from the chain+address (NEVER matched on login) and a
    /// random non-usable password hash — there is no password login path for a
    /// wallet-only avatar. <c>OwnerTenantId == null</c> by construction.
    /// </summary>
    private async Task<AZOAResult<IAvatar>> CreateSelfOwnedWalletAvatarAsync(
        string address, string chainType, CancellationToken ct)
    {
        // A short, unique-ish suffix keeps the synthesized handle readable while the
        // full address guarantees global uniqueness.
        var prefix = address.Length <= 12 ? address : address[..12];
        var unique = Guid.NewGuid().ToString("N")[..8];
        var handle = $"wallet-{chainType}-{prefix}-{unique}".ToLowerInvariant();

        var avatar = new Avatar
        {
            Id = Guid.NewGuid(),
            Username = handle,
            Email = $"{handle}@wallet.azoa.local",
            // No password login for a wallet-only avatar; random hash keeps the column
            // non-empty without granting a usable password.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            IsVerified = false,
            // Self-owned by construction — the durable state for all users (AC2/§3).
            OwnerTenantId = null,
            AuthWalletAddress = address,
            AuthWalletChainType = chainType,
        };

        return await _avatarStore.UpsertAsync(avatar, ct);
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

    // ── Login JWT (mirrors AvatarManager.GenerateJwt exactly) ─────────────────

    private string GenerateLoginJwt(IAvatar avatar)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, avatar.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, avatar.Email),
            new Claim(ClaimTypes.Name, avatar.Username),
            // token-type segregation (security-review S5): a login token and a tenant
            // child credential share key/issuer/audience/alg — distinguishing them ONLY
            // by act_as_tenant is fragile. The explicit token_use claim makes the class
            // unambiguous (full-authority user login vs scoped tenant child). It never
            // carries act_as_tenant, so it can never be mistaken for a tenant-driven token.
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
