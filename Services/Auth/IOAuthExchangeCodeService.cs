// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Mints and redeems the single-use handoff code used by the OAuth callback.</summary>
public interface IOAuthExchangeCodeService
{
    /// <summary>Mints a code and returns the PLAINTEXT; only its hash is persisted.</summary>
    Task<AZOAResult<string>> MintAsync(
        OAuthExchangeCodePurpose purpose,
        Guid avatarId,
        ExternalAuthProvider provider,
        Guid? tenantId,
        string tenantScope,
        string? returnTo,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes a code of the expected purpose. Every rejection reason
    /// returns the same message so a caller cannot probe which codes exist.
    /// </summary>
    Task<AZOAResult<OAuthExchangeCode>> RedeemAsync(
        string code,
        OAuthExchangeCodePurpose expectedPurpose,
        CancellationToken ct = default);
}
