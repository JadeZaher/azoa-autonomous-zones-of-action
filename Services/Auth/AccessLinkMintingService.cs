// SPDX-License-Identifier: UNLICENSED

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Compliance;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Mints and verifies HMAC-SHA256 signed ride-along access links scoped to commercial
/// and identity workflows (<c>kyc</c>, <c>consent</c>, <c>add_funds</c>, <c>cash_out</c>,
/// <c>invoice_pay</c>, <c>receipt_view</c>, <c>statement_view</c>).
/// </summary>
public sealed class AccessLinkMintingService
{
    private const int DefaultTtlMinutes = 60;
    private const int MaxTtlMinutes = 7 * 24 * 60;
    private static readonly byte[] HkdfInfo = "azoa:commercial:access-link:hmac-sha256:v1"u8.ToArray();
    private static readonly byte[] EphemeralUnitFallbackSeed = "azoa:ephemeral-unit-access-link-seed:v1"u8.ToArray();

    private readonly IProviderDeferralLedger _deferrals;
    private readonly MemberHandoffOptions _handoffOptions;
    private readonly TimeProvider _clock;
    private readonly byte[] _hmacKey;
    private readonly ConcurrentDictionary<string, AccessLinkMetadata> _metadata = new(StringComparer.Ordinal);

    private sealed record AccessLinkMetadata(
        string? TargetResourceId,
        string? ReturnUrl,
        long ExpUnix,
        string? OwnerActorId,
        SignedAccessLink Link);

    public AccessLinkMintingService(
        IConfiguration configuration,
        IProviderDeferralLedger deferrals,
        MemberHandoffOptions? handoffOptions = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _deferrals = deferrals ?? throw new ArgumentNullException(nameof(deferrals));
        _handoffOptions = handoffOptions ?? MemberHandoffOptions.FromConfiguration(configuration);
        _clock = clock ?? TimeProvider.System;
        _hmacKey = DeriveSigningKey(configuration);
    }

    public AZOAResult<SignedAccessLink> Mint(MintAccessLinkRequest request, string? ownerActorId = null)
    {
        if (request is null)
            return AZOAResult<SignedAccessLink>.FailureWithCode(
                "An access link request is required.", AzoaErrorCodes.InvalidRequest);

        if (string.IsNullOrWhiteSpace(request.ExternalSubject)
            || request.ExternalSubject.Trim().Length > 128
            || request.ExternalSubject.Any(char.IsControl))
        {
            return AZOAResult<SignedAccessLink>.FailureWithCode(
                "A valid externalSubject is required.", AzoaErrorCodes.InvalidRequest);
        }

        var scope = request.Scope?.Trim() ?? string.Empty;
        if (!AccessLinkScopes.All.Contains(scope))
        {
            return AZOAResult<SignedAccessLink>.FailureWithCode(
                $"Unsupported access link scope '{request.Scope}'.", AzoaErrorCodes.InvalidRequest);
        }

        if (!TryValidateOptionalReturnUrl(request.ReturnUrl, out var validatedReturnUrl))
        {
            return AZOAResult<SignedAccessLink>.FailureWithCode(
                "returnUrl must be a valid HTTP or HTTPS URL.", AzoaErrorCodes.InvalidRequest);
        }

        var externalSubject = request.ExternalSubject.Trim();
        var targetResourceId = NormalizeTargetResourceId(request.TargetResourceId);

        var ttlMinutes = Math.Clamp(request.TtlMinutes ?? DefaultTtlMinutes, 1, MaxTtlMinutes);
        var createdAt = _clock.GetUtcNow();
        var expiresAt = createdAt.AddMinutes(ttlMinutes);
        var expUnix = expiresAt.ToUnixTimeSeconds();
        var linkId = Guid.NewGuid().ToString("N");

        var signatureBytes = ComputeSignature(linkId, scope, externalSubject, expUnix);
        var signature = EncodeBase64Url(signatureBytes);
        var rideAlongToken = $"{linkId}.{expUnix}.{signature}";

        var query = new StringBuilder();
        query.Append("scope=").Append(Uri.EscapeDataString(scope));
        query.Append("&sub=").Append(Uri.EscapeDataString(externalSubject));
        query.Append("&exp=").Append(expUnix.ToString(CultureInfo.InvariantCulture));
        query.Append("&sig=").Append(Uri.EscapeDataString(signature));
        if (targetResourceId is not null)
            query.Append("&res=").Append(Uri.EscapeDataString(targetResourceId));
        if (validatedReturnUrl is not null)
            query.Append("&returnUrl=").Append(Uri.EscapeDataString(validatedReturnUrl));

        var relativeUrl = $"/ride/{Uri.EscapeDataString(linkId)}?{query}";
        var baseUrl = _handoffOptions.HostedBaseUrl?.Trim().TrimEnd('/');
        var signedUrl = !string.IsNullOrWhiteSpace(baseUrl)
            ? $"{baseUrl}{relativeUrl}"
            : relativeUrl;

        var hasLiveIdentomat = _deferrals.HasLiveIdentomatCredentials;
        if (!hasLiveIdentomat)
            _deferrals.EnsureIdentomatCredentialDeferralEvaluated();

        var deferralStatus = hasLiveIdentomat
            ? AccessLinkDeferralStatuses.Active
            : AccessLinkDeferralStatuses.DeferredCredentials;
        var deferredReason = hasLiveIdentomat
            ? null
            : ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode;

        var nowUnix = createdAt.ToUnixTimeSeconds();
        if (_metadata.Count >= 4096)
        {
            foreach (var entry in _metadata)
            {
                if (entry.Value.ExpUnix <= nowUnix)
                    _metadata.TryRemove(entry.Key, out _);
            }

            if (_metadata.Count >= 4096)
            {
                foreach (var oldest in _metadata.OrderBy(e => e.Value.ExpUnix).Take(_metadata.Count - 4095))
                    _metadata.TryRemove(oldest.Key, out _);
            }
        }
        var link = new SignedAccessLink(
            linkId,
            externalSubject,
            scope,
            targetResourceId,
            validatedReturnUrl,
            signedUrl,
            signature,
            rideAlongToken,
            expiresAt,
            createdAt,
            ProviderDeferralLedger.IdentomatProviderName,
            deferralStatus,
            deferredReason);

        var normalizedOwnerId = string.IsNullOrWhiteSpace(ownerActorId) ? null : ownerActorId.Trim();
        _metadata[linkId] = new AccessLinkMetadata(targetResourceId, validatedReturnUrl, expUnix, normalizedOwnerId, link);

        return AZOAResult<SignedAccessLink>.Success(link);
    }

    public IReadOnlyList<SignedAccessLink> ListRecentLinks(string? ownerActorId = null)
    {
        var nowUnix = _clock.GetUtcNow().ToUnixTimeSeconds();
        var normalizedOwner = string.IsNullOrWhiteSpace(ownerActorId) ? null : ownerActorId.Trim();
        return _metadata.Values
            .Where(m => m.ExpUnix > nowUnix
                && (normalizedOwner is null
                    || string.Equals(m.OwnerActorId, normalizedOwner, StringComparison.Ordinal)
                    || string.Equals(m.Link.ExternalSubject, normalizedOwner, StringComparison.Ordinal)))
            .Select(m => m.Link)
            .OrderByDescending(l => l.CreatedAt)
            .ToArray();
    }

    public AZOAResult<AccessLinkVerificationResult> VerifyAccessLink(
        string linkId,
        string scope,
        string externalSubject,
        long exp,
        string sig,
        string? targetResourceId = null,
        string? returnUrl = null)
    {
        var hasLiveIdentomat = _deferrals.HasLiveIdentomatCredentials;
        var deferralStatus = hasLiveIdentomat
            ? AccessLinkDeferralStatuses.Active
            : AccessLinkDeferralStatuses.DeferredCredentials;
        var deferredReason = hasLiveIdentomat
            ? null
            : ProviderDeferralLedger.IdentomatCredentialsPendingReasonCode;

        var safeExp = Math.Clamp(exp, 0, 253402300799L);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(safeExp);

        if (string.IsNullOrWhiteSpace(linkId)
            || string.IsNullOrWhiteSpace(scope)
            || string.IsNullOrWhiteSpace(externalSubject)
            || string.IsNullOrWhiteSpace(sig)
            || !AccessLinkScopes.All.Contains(scope.Trim()))
        {
            return AZOAResult<AccessLinkVerificationResult>.FailureWithCode(
                "Malformed access link parameters.",
                AzoaErrorCodes.InvalidRequest,
                new AccessLinkVerificationResult(
                    false,
                    linkId ?? string.Empty,
                    scope ?? string.Empty,
                    externalSubject ?? string.Empty,
                    expiresAt,
                    ProviderDeferralLedger.IdentomatProviderName,
                    deferralStatus,
                    deferredReason,
                    FailureReason: AzoaErrorCodes.InvalidRequest));
        }

        var trimmedLinkId = linkId.Trim();
        var trimmedScope = scope.Trim();
        var trimmedSubject = externalSubject.Trim();
        var expected = ComputeSignature(trimmedLinkId, trimmedScope, trimmedSubject, exp);

        if (!TryDecodeBase64Url(sig.Trim(), out var provided)
            || provided.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(expected, provided))
        {
            return AZOAResult<AccessLinkVerificationResult>.FailureWithCode(
                "Access link signature verification failed.",
                "INVALID_SIGNATURE",
                new AccessLinkVerificationResult(
                    false,
                    trimmedLinkId,
                    trimmedScope,
                    trimmedSubject,
                    expiresAt,
                    ProviderDeferralLedger.IdentomatProviderName,
                    deferralStatus,
                    deferredReason,
                    FailureReason: "INVALID_SIGNATURE"));
        }

        if (expiresAt <= _clock.GetUtcNow())
        {
            return AZOAResult<AccessLinkVerificationResult>.FailureWithCode(
                "Access link has expired.",
                "LINK_EXPIRED",
                new AccessLinkVerificationResult(
                    false,
                    trimmedLinkId,
                    trimmedScope,
                    trimmedSubject,
                    expiresAt,
                    ProviderDeferralLedger.IdentomatProviderName,
                    deferralStatus,
                    deferredReason,
                    FailureReason: "LINK_EXPIRED"));
        }

        _metadata.TryGetValue(trimmedLinkId, out var meta);
        var resolvedTargetResourceId = meta?.TargetResourceId ?? NormalizeTargetResourceId(targetResourceId);
        var resolvedReturnUrl = meta?.ReturnUrl;
        if (resolvedReturnUrl is null && TryValidateOptionalReturnUrl(returnUrl, out var parsedReturnUrl))
            resolvedReturnUrl = parsedReturnUrl;

        return AZOAResult<AccessLinkVerificationResult>.Success(new AccessLinkVerificationResult(
            true,
            trimmedLinkId,
            trimmedScope,
            trimmedSubject,
            expiresAt,
            ProviderDeferralLedger.IdentomatProviderName,
            deferralStatus,
            deferredReason,
            resolvedTargetResourceId,
            resolvedReturnUrl));
    }

    private static string? NormalizeTargetResourceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl))
            return null;

        var trimmed = raw.Trim();
        return trimmed.Length <= 128 ? trimmed : null;
    }

    private static bool TryValidateOptionalReturnUrl(string? raw, out string? validatedReturnUrl)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            validatedReturnUrl = null;
            return true;
        }

        var candidate = raw.Trim();
        if (candidate.Any(char.IsControl)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            validatedReturnUrl = null;
            return false;
        }

        validatedReturnUrl = uri.ToString();
        return true;
    }

    private byte[] ComputeSignature(string linkId, string scope, string externalSubject, long expUnixSeconds)
    {
        var canonical = $"{linkId}\n{scope}\n{externalSubject}\n{expUnixSeconds.ToString(CultureInfo.InvariantCulture)}";
        return HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(canonical));
    }

    private static byte[] DeriveSigningKey(IConfiguration configuration)
    {
        var jwtKey = configuration["Jwt:Key"];
        byte[] ikm;
        if (!string.IsNullOrWhiteSpace(jwtKey))
        {
            ikm = Encoding.UTF8.GetBytes(jwtKey);
        }
        else
        {
            var walletKey = configuration["AZOA:WalletEncryptionKey"];
            ikm = !string.IsNullOrWhiteSpace(walletKey)
                ? Encoding.UTF8.GetBytes(walletKey)
                : EphemeralUnitFallbackSeed;
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, info: HkdfInfo);
    }

    private static string EncodeBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                return false;
        }

        var buffer = new byte[(padded.Length / 4) * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
            return false;

        bytes = buffer[..written];
        return true;
    }
}
