// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence for the single-use OAuth handoff code.
///
/// <para>There is no "get" that leaves the row live: <see cref="TryConsumeAsync"/> is
/// the only read path and it consumes atomically, so a code can never be observed
/// twice. This mirrors <c>IWalletAuthChallengeStore.TryConsumeAsync</c>.</para>
/// </summary>
public interface IOAuthExchangeCodeStore
{
    /// <summary>Persists a freshly minted code (hashed by the caller).</summary>
    Task<AZOAResult<OAuthExchangeCode>> CreateAsync(
        OAuthExchangeCode code,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically consumes a live code and returns the row it consumed, or a null
    /// result when the code is unknown, already consumed, or expired. The three
    /// failure modes are deliberately indistinguishable to the caller.
    /// </summary>
    Task<AZOAResult<OAuthExchangeCode?>> TryConsumeAsync(
        string codeHash,
        DateTimeOffset now,
        CancellationToken ct = default);
}
