// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Mints and redeems the single-use OAuth handoff code.</summary>
/// <remarks>
/// <para>The OAuth callback is a top-level browser navigation, so the only way to hand
/// a result to a single-page app is a URL — and a JWT in a URL lands in browser
/// history, the <c>Referer</c> header, and every proxy log on the path. The callback
/// therefore carries an opaque, short-TTL, single-use code that the SPA immediately
/// POSTs back for the real token.</para>
///
/// <para>Only the SHA-256 of the code is stored, exactly as <c>ApiKey</c> stores a
/// hashed key: a database read must never yield a usable credential.</para>
/// </remarks>
public sealed class OAuthExchangeCodeService : IOAuthExchangeCodeService
{
    /// <summary>256 bits from the CSPRNG; the code is a bearer credential.</summary>
    private const int CodeByteLength = 32;

    private readonly IOAuthExchangeCodeStore _store;
    private readonly ExternalAuthOptions _options;
    private readonly TimeProvider _clock;

    public OAuthExchangeCodeService(
        IOAuthExchangeCodeStore store,
        IOptions<ExternalAuthOptions> options,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<string>> MintAsync(
        OAuthExchangeCodePurpose purpose,
        Guid avatarId,
        ExternalAuthProvider provider,
        Guid? tenantId,
        string tenantScope,
        string? returnTo,
        CancellationToken ct = default)
    {
        var code = GenerateCode();
        var now = _clock.GetUtcNow();

        var row = new OAuthExchangeCode
        {
            Id = Guid.NewGuid().ToString("N"),
            CodeHash = HashCode(code),
            Purpose = purpose,
            AvatarId = avatarId.ToString("N"),
            Provider = provider,
            TenantScope = tenantScope,
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            ReturnTo = returnTo,
            ExpiresAt = now.AddSeconds(Math.Clamp(_options.ExchangeCodeTtlSeconds, 30, 600)),
            CreatedAt = now,
        };

        var saved = await _store.CreateAsync(row, ct);
        return saved.IsError
            ? AZOAResult<string>.FailureWithCode(saved.Message, AzoaErrorCodes.DependencyUnavailable)
            : AZOAResult<string>.Success(code, "Minted.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<OAuthExchangeCode>> RedeemAsync(
        string code,
        OAuthExchangeCodePurpose expectedPurpose,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AZOAResult<OAuthExchangeCode>.FailureWithCode(
                InvalidCodeMessage, AzoaErrorCodes.InvalidRequest);

        var consumed = await _store.TryConsumeAsync(HashCode(code), _clock.GetUtcNow(), ct);
        if (consumed.IsError)
            return AZOAResult<OAuthExchangeCode>.FailureWithCode(
                consumed.Message, AzoaErrorCodes.DependencyUnavailable);

        if (consumed.Result is null)
            return AZOAResult<OAuthExchangeCode>.FailureWithCode(
                InvalidCodeMessage, AzoaErrorCodes.InvalidRequest);

        if (consumed.Result.Purpose != expectedPurpose)
        {
            // The code was consumed regardless — a purpose-confused code is spent, not
            // returned to the pool, so probing for one cannot be repeated.
            return AZOAResult<OAuthExchangeCode>.FailureWithCode(
                InvalidCodeMessage, AzoaErrorCodes.InvalidRequest);
        }

        return AZOAResult<OAuthExchangeCode>.Success(consumed.Result, "Redeemed.");
    }

    /// <summary>One message for every rejection: unknown, expired, spent, or wrong purpose.</summary>
    internal const string InvalidCodeMessage = "This sign-in link is invalid or has expired. Start again.";

    private static string GenerateCode()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(CodeByteLength));

    /// <summary>SHA-256 hex of the code; the plaintext never reaches the database.</summary>
    internal static string HashCode(string code)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>URL-safe base64 without padding, so the code survives a query string.</summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
