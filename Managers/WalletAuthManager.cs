using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class WalletAuthManager : IWalletAuthManager
{
    private const string DomainPrefix = "AZOA-AUTH-v1";

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ClaimTokenLifetime = TimeSpan.FromMinutes(15);

    private const int MaxLiveChallengesPerAddress = 5;

    private readonly IWalletAuthChallengeStore _challenges;
    private readonly IWalletAuthClaimTokenStore _claimTokens;
    private readonly IUserStore _userStore;
    private readonly IWalletSignatureVerifier _signatureVerifier;
    private readonly IConfiguration _config;

    public WalletAuthManager(
        IWalletAuthChallengeStore challenges,
        IWalletAuthClaimTokenStore claimTokens,
        IUserStore userStore,
        IWalletSignatureVerifier signatureVerifier,
        IConfiguration config)
    {
        _challenges = challenges ?? throw new ArgumentNullException(nameof(challenges));
        _claimTokens = claimTokens ?? throw new ArgumentNullException(nameof(claimTokens));
        _userStore = userStore ?? throw new ArgumentNullException(nameof(userStore));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }


    /// <inheritdoc/>
    public async Task<AZOAResult<WalletChallengeResponse>> CreateChallengeAsync(
        string address, string chainType, CancellationToken ct = default)
    {
        var result = new AZOAResult<WalletChallengeResponse>();

        if (!AZOA.WebAPI.Helpers.WalletAuthIdentity.TryCreate(address, chainType, out var addr, out var chain))
        {
            result.IsError = true;
            result.Message = "address and chainType are required.";
            return result;
        }

        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime;

        var liveCount = await _challenges.CountLiveByAddressAsync(addr, chain, now, ct);
        if (liveCount.IsError) return AZOAResult<WalletChallengeResponse>.Failure(liveCount.Message);
        if (liveCount.Result >= MaxLiveChallengesPerAddress)
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


    /// <inheritdoc/>
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
            if (bound.Result.Id == NodeOperatorIdentity.UserId || !bound.Result.IsActive)
            {
                result.IsError = true;
                result.Message = "Wallet authentication is not available for this identity.";
                return result;
            }
            user = bound.Result;
        }
        else
        {
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


    /// <inheritdoc/>
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

        var loaded = await _userStore.GetByIdAsync(authedUserId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = "Authenticated user not found.";
            result.Exception = loaded.Exception;
            return result;
        }

        var user = loaded.Result;
        var expectedAddress = user.AuthWalletAddress;
        var expectedChain = user.AuthWalletChainType;
        var expectedTenant = user.OwnerTenantId;
        var expectedAuthNotBefore = user.AuthNotBefore;
        user.AuthWalletAddress = addr;
        user.AuthWalletChainType = chain;

        var saved = await _userStore.TryUpdateAuthenticationAsync(user, expectedAddress, expectedChain, expectedTenant, expectedAuthNotBefore, ct);
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


    /// <inheritdoc/>
    public async Task<AZOAResult<ClaimInviteResponse>> CreateClaimInviteAsync(
        Guid tenantId, Guid childUserId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ClaimInviteResponse>();

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


    /// <inheritdoc/>
    public async Task<AZOAResult<WalletAuthTokenResponse>> ClaimAsync(
        Guid expectedUserId,
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

        if (string.IsNullOrWhiteSpace(claimToken))
            return AZOAResult<WalletAuthTokenResponse>.Failure("A live claim invite is required.");
        var tokenRow = await _claimTokens.GetByTokenAsync(claimToken.Trim(), ct);
        if (tokenRow.IsError) return AZOAResult<WalletAuthTokenResponse>.Failure(tokenRow.Message);
        if (tokenRow.Result is not { } invite || !invite.IsLiveAt(now))
            return AZOAResult<WalletAuthTokenResponse>.Failure(TenantAuthorizationError.NotFound + "Invalid or expired claim token.");
        if (expectedUserId == Guid.Empty || invite.TargetUserId != expectedUserId)
            return AZOAResult<WalletAuthTokenResponse>.Failure(TenantAuthorizationError.NotFound + "Invalid or expired claim token.");
        var targetUserId = invite.TargetUserId;
        var invitedTenant = invite.TenantId;

        if (targetUserId == NodeOperatorIdentity.UserId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such user to claim.";
            return result;
        }

        var loaded = await _userStore.GetByIdAsync(targetUserId, ct);
        if (loaded.IsError || loaded.Result is null)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such user to claim.";
            result.Exception = loaded.Exception;
            return result;
        }
        var user = loaded.Result;
        var expectedAddress = user.AuthWalletAddress;
        var expectedChain = user.AuthWalletChainType;
        var expectedTenant = user.OwnerTenantId;
        var expectedAuthNotBefore = user.AuthNotBefore;

        if (!expectedTenant.HasValue || invitedTenant != expectedTenant || !user.IsActive)
            return AZOAResult<WalletAuthTokenResponse>.FailureWithCode("The user is no longer claimable.", AzoaErrorCodes.Conflict);
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
            user.AuthWalletAddress = null;
            user.AuthWalletChainType = null;
        }
        else
        {
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
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        }

        user.OwnerTenantId = null;          // IDOR-safe sever (AC3)
        user.AuthNotBefore = expectedAuthNotBefore > now ? expectedAuthNotBefore : now;
        user.IsActive = true;

        var saved = await _userStore.TryClaimAuthenticationAsync(user, expectedAddress, expectedChain, expectedTenant.Value, expectedAuthNotBefore, invite, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to claim user.";
            result.Exception = saved.Exception;
            return result;
        }


        result.Result = new WalletAuthTokenResponse
        {
            Token = GenerateLoginJwt(saved.Result),
            UserId = saved.Result.Id,
        };
        result.Message = "User claimed.";
        return result;
    }


    private async Task<AZOAResult<WalletAuthChallenge>> ConsumeAndVerifyAsync(
        string address, string chainType, string signature, string? clientMessage, CancellationToken ct)
    {
        var result = new AZOAResult<WalletAuthChallenge>();

        if (!AZOA.WebAPI.Helpers.WalletAuthIdentity.TryCreate(address, chainType, out var addr, out var chain) || string.IsNullOrWhiteSpace(signature))
        {
            result.IsError = true;
            result.Message = "address, chainType and signature are required.";
            return result;
        }

        var now = DateTime.UtcNow;

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

        var boundChain = string.Equals(challenge.ChainType, chain, StringComparison.Ordinal);
        var boundAddress = string.Equals(challenge.Address, addr, StringComparison.Ordinal);
        var msg = challenge.DomainMessage;
        var wellFormed = string.Equals(msg, BuildDomainMessage(chain, addr, challenge.Nonce, challenge.ExpiresAt), StringComparison.Ordinal);
        if (!boundChain || !boundAddress || !wellFormed)
        {
            result.IsError = true;
            result.Message = "Challenge validation failed.";
            return result;
        }
        if (clientMessage is not null && !string.Equals(clientMessage, challenge.DomainMessage, StringComparison.Ordinal))
        {
            result.IsError = true;
            result.Message = "Signed message does not match the issued challenge.";
            return result;
        }

        var consumed = await _challenges.TryConsumeAsync(challenge.Nonce, now, ct);
        if (consumed.IsError || !consumed.Result)
        {
            result.IsError = true;
            result.Message = "Challenge already used or expired. Request a new challenge.";
            result.Exception = consumed.Exception;
            return result;
        }

        if (!AZOA.WebAPI.Helpers.ByteEncoding.TryDecodeSignature(signature, out var signatureBytes))
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
            result.IsError = true;
            result.Message = "Signature verification failed.";
            return result;
        }

        result.Result = challenge;
        result.Message = "Verified.";
        return result;
    }


    private async Task<AZOAResult<IUser>> CreateSelfOwnedWalletUserAsync(
        string address, string chainType, CancellationToken ct)
    {
        var prefix = address.Length <= 12 ? address : address[..12];
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"azoa-auth-wallet:v1:{chainType}:{address}"));
        var userId = new Guid(digest.AsSpan(0, 16));
        var unique = userId.ToString("N")[..8];
        var handle = $"wallet-{chainType}-{prefix}-{unique}".ToLowerInvariant();

        var user = new User
        {
            Id = userId,
            Username = handle,
            Email = $"{handle}@wallet.azoa.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsActive = true,
            IsVerified = false,
            OwnerTenantId = null,
            AuthWalletAddress = address,
            AuthWalletChainType = chainType,
        };

        var created = await _userStore.CreateIfAbsentAsync(user, ct);
        if (!created.IsError)
            return created.Result is { IsActive: true } existing && existing.AuthWalletAddress == address && existing.AuthWalletChainType == chainType
                ? created : AZOAResult<IUser>.Failure("The wallet identity admission conflicts with an existing account.");
        var winner = await _userStore.GetByAuthWalletAsync(address, chainType, ct);
        return !winner.IsError && winner.Result is { IsActive: true } ? winner : created;
    }


    private string BuildDomainMessage(string chainType, string address, string nonce, DateTime expiresAtUtc)
    {
        var issuer = _config["Jwt:Issuer"] ?? string.Empty;
        var audience = _config["Jwt:Audience"] ?? string.Empty;
        var expiryIso = expiresAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

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


    private string GenerateLoginJwt(IUser user)
        => AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer.BuildLoginToken(_config, user);
}
