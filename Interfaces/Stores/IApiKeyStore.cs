using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Per-aggregate storage seam for <see cref="ApiKey"/>.
///
/// Consumers (<c>ApiKeyAuthenticationHandler</c>, <c>ApiKeyController</c>) MUST
/// inject this interface rather than the underlying persistence context — that
/// way the wave-3 EF → SurrealDB cutover is a one-line DI change rather than a
/// caller rewrite (mirroring the IBridgeStore / IIdempotencyStore pattern).
///
/// Lifecycle filtering (active-and-valid) is the caller's responsibility:
/// <see cref="GetByHashAsync"/> returns the row regardless of <c>IsActive</c>,
/// <c>RevokedAt</c>, or <c>ExpiresAt</c>; the handler reads those fields and
/// applies the policy so failure-mode distinction (revoked vs. expired vs.
/// invalid) is preserved in the AuthenticateResult message.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Look up an API key by its SHA-256 hash. Returns <c>null</c> when no row
    /// matches. The caller MUST inspect <see cref="ApiKey.IsActive"/>,
    /// <see cref="ApiKey.RevokedAt"/>, and <see cref="ApiKey.ExpiresAt"/> before
    /// granting authentication.
    /// </summary>
    Task<ApiKey?> GetByHashAsync(string keyHash, CancellationToken ct);

    /// <summary>
    /// List every key owned by an avatar, newest first. Includes revoked and
    /// expired keys (the management UI shows them with a status badge).
    /// </summary>
    Task<IReadOnlyList<ApiKey>> ListByAvatarAsync(Guid avatarId, CancellationToken ct);

    /// <summary>
    /// Fetch one key by id, scoped to an avatar — returns <c>null</c> if the
    /// key does not belong to that avatar (prevents cross-account access).
    /// </summary>
    Task<ApiKey?> GetByIdForAvatarAsync(Guid id, Guid avatarId, CancellationToken ct);

    /// <summary>
    /// Persist a freshly-minted key. Throws if a row with the same
    /// <see cref="ApiKey.KeyHash"/> already exists (UNIQUE-index violation —
    /// cryptographically improbable, but surfaced rather than silently
    /// overwritten).
    /// </summary>
    Task CreateAsync(ApiKey apiKey, CancellationToken ct);

    /// <summary>
    /// Soft-delete a key: sets <c>IsActive = false</c> and <c>RevokedAt = revokedAt</c>.
    /// Returns <c>true</c> when the row existed and belonged to the avatar,
    /// <c>false</c> otherwise.
    /// </summary>
    Task<bool> RevokeAsync(Guid id, Guid avatarId, DateTime revokedAt, CancellationToken ct);

    /// <summary>
    /// Hard-delete a key row. Returns <c>true</c> when the row existed and
    /// belonged to the avatar.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, Guid avatarId, CancellationToken ct);

    /// <summary>
    /// Update the <c>LastUsedAt</c> timestamp. Fire-and-forget — this method
    /// MUST NOT throw under any failure mode. The handler invokes it on a
    /// detached <see cref="Task.Run"/> after a successful authenticate; a
    /// throw would surface as an unobserved-task exception and is the wrong
    /// failure semantics for an auxiliary timestamp.
    /// </summary>
    Task TouchLastUsedAsync(Guid id, DateTime lastUsedAt, CancellationToken ct);
}
