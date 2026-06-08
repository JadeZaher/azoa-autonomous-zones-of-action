using System.Text.Json.Serialization;
using Oasis.SurrealDb.Client.Query;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Models;

namespace OASIS.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IApiKeyStore"/>.
///
/// Pattern: mirrors <see cref="SurrealNftStore"/> / <see cref="SurrealSagaStore"/>
///   — Guid("N") lowercase-hex record ids and inline POCO until the
///   source-generator catches up to table 120. Replace the inline POCO with the
///   generated type when <c>OASIS.WebAPI.Persistence.SurrealDb.Models.ApiKey</c> arrives.
///
/// Lookups:
///   - <see cref="GetByHashAsync"/> uses the UNIQUE <c>api_key_unique_hash</c>
///     index; cryptographically O(1).
///   - <see cref="ListByAvatarAsync"/> uses the <c>api_key_by_avatar</c>
///     index with an ORDER BY created_date DESC.
///   - <see cref="GetByIdForAvatarAsync"/> is a single record-id SELECT with a
///     post-read avatar check (avoids two round-trips when the row exists).
///
/// Mutations:
///   - <see cref="CreateAsync"/> uses CREATE; UNIQUE collision on key_hash
///     surfaces a SurrealDB ERR which we rethrow as
///     <see cref="InvalidOperationException"/>.
///   - <see cref="RevokeAsync"/> is a single-field-pair UPDATE scoped to the
///     owning avatar (no cross-account writes).
///   - <see cref="DeleteAsync"/> is a DELETE scoped to the owning avatar.
///   - <see cref="TouchLastUsedAsync"/> swallows every failure (contract: must
///     not throw — see interface XML doc); errors are silently dropped so the
///     handler's fire-and-forget pattern is honoured.
/// </summary>
public sealed class SurrealApiKeyStore : IApiKeyStore
{
    private const string Table = "api_key";

    private readonly ISurrealExecutor _executor;

    public SurrealApiKeyStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ApiKey?> GetByHashAsync(string keyHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyHash)) return null;

        var q = SurrealQuery
            .Of("SELECT * FROM api_key WHERE key_hash = $_hash LIMIT 1")
            .WithParam("_hash", keyHash);

        var rows = await _executor.QueryAsync<ApiKeyPoco>(q, ct);
        return rows.Count == 0 ? null : ToDomain(rows[0]);
    }

    public async Task<IReadOnlyList<ApiKey>> ListByAvatarAsync(Guid avatarId, CancellationToken ct)
    {
        var q = SurrealQuery
            .Of("SELECT * FROM api_key WHERE avatar_id = $_avatar ORDER BY created_date DESC")
            .WithParam("_avatar", ToSurrealId(avatarId));

        var rows = await _executor.QueryAsync<ApiKeyPoco>(q, ct);
        var result = new List<ApiKey>(rows.Count);
        foreach (var row in rows) result.Add(ToDomain(row));
        return result;
    }

    public async Task<ApiKey?> GetByIdForAvatarAsync(Guid id, Guid avatarId, CancellationToken ct)
    {
        var surrealId = ToSurrealId(id);
        var q = SurrealQuery
            .Of("SELECT * FROM type::record($_t, $_id)")
            .WithParam("_t", Table)
            .WithParam("_id", surrealId);

        var rows = await _executor.QueryAsync<ApiKeyPoco>(q, ct);
        if (rows.Count == 0) return null;

        var poco = rows[0];
        var avatarHex = ToSurrealId(avatarId);
        // Scoped to owner — surface as "not found" rather than "forbidden" so
        // a cross-avatar probe cannot distinguish "no such key" from
        // "exists but belongs to someone else".
        return string.Equals(poco.AvatarId, avatarHex, StringComparison.OrdinalIgnoreCase)
            ? ToDomain(poco)
            : null;
    }

    public async Task CreateAsync(ApiKey apiKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var poco = FromDomain(apiKey);

        var q = SurrealQuery
            .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
            .WithParam("_t", Table)
            .WithParam("_id", poco.Id)
            .WithParam("_body", poco);

        var response = await _executor.ExecuteAsync(q, ct);

        if (!response[0].IsOk)
        {
            var detail = response[0].Detail ?? string.Empty;
            // Surface UNIQUE-on-key_hash collision explicitly so callers can
            // tell "duplicate key hash" apart from generic DB failure. A real
            // SHA-256 collision is cryptographically improbable; in practice
            // this fires when an upstream bug re-uses the same raw key.
            throw new InvalidOperationException(
                $"SurrealApiKeyStore.CreateAsync failed for key id {apiKey.Id}: " +
                $"SurrealDB returned ERR: {detail}");
        }
    }

    public async Task<bool> RevokeAsync(Guid id, Guid avatarId, DateTime revokedAt, CancellationToken ct)
    {
        var surrealId = ToSurrealId(id);
        var avatarHex = ToSurrealId(avatarId);
        var revokedUtc = DateTime.SpecifyKind(revokedAt, DateTimeKind.Utc);

        var q = SurrealQuery
            .Of("UPDATE type::record($_t, $_id) SET is_active = false, revoked_at = $_revoked WHERE avatar_id = $_avatar RETURN AFTER")
            .WithParam("_t", Table)
            .WithParam("_id", surrealId)
            .WithParam("_avatar", avatarHex)
            .WithParam("_revoked", revokedUtc);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid avatarId, CancellationToken ct)
    {
        var surrealId = ToSurrealId(id);
        var avatarHex = ToSurrealId(avatarId);

        var q = SurrealQuery
            .Of("DELETE type::record($_t, $_id) WHERE avatar_id = $_avatar RETURN BEFORE")
            .WithParam("_t", Table)
            .WithParam("_id", surrealId)
            .WithParam("_avatar", avatarHex);

        var response = await _executor.ExecuteAsync(q, ct);
        if (response.Count == 0 || !response[0].IsOk) return false;
        return response[0].AffectedCount() == 1;
    }

    public async Task TouchLastUsedAsync(Guid id, DateTime lastUsedAt, CancellationToken ct)
    {
        // Contract: this method MUST NOT throw. The handler invokes it on a
        // detached Task.Run after a successful authenticate; a throw would
        // surface as an unobserved-task exception and is the wrong failure
        // semantics for an auxiliary timestamp. We swallow every category of
        // error — connection failure, DB error, cancellation — silently. The
        // worst case is a stale last_used_at, which the management UI tolerates.
        try
        {
            var surrealId = ToSurrealId(id);
            var lastUtc = DateTime.SpecifyKind(lastUsedAt, DateTimeKind.Utc);

            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET last_used_at = $_now")
                .WithParam("_t", Table)
                .WithParam("_id", surrealId)
                .WithParam("_now", lastUtc);

            await _executor.ExecuteAsync(q, ct);
        }
        catch
        {
            // Intentionally swallowed — see contract above.
        }
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
    {
        var stripped = StripIdPrefix(id);
        return Guid.ParseExact(stripped, "N");
    }

    /// <summary>
    /// Strips a leading "api_key:" prefix if SurrealDB returned the id in
    /// thing-form rather than as the bare suffix. Defensive — both shapes
    /// appear in practice depending on the serializer codepath.
    /// </summary>
    private static string StripIdPrefix(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var colon = raw.IndexOf(':');
        return colon >= 0 && colon < raw.Length - 1 ? raw[(colon + 1)..] : raw;
    }

    private static ApiKeyPoco FromDomain(ApiKey k) => new()
    {
        Id          = ToSurrealId(k.Id),
        AvatarId    = ToSurrealId(k.AvatarId),
        Name        = k.Name ?? string.Empty,
        KeyHash     = k.KeyHash ?? string.Empty,
        KeyPrefix   = k.KeyPrefix ?? string.Empty,
        CreatedDate = new DateTimeOffset(DateTime.SpecifyKind(k.CreatedDate, DateTimeKind.Utc)),
        ExpiresAt   = k.ExpiresAt.HasValue
                      ? new DateTimeOffset(DateTime.SpecifyKind(k.ExpiresAt.Value, DateTimeKind.Utc))
                      : null,
        LastUsedAt  = k.LastUsedAt.HasValue
                      ? new DateTimeOffset(DateTime.SpecifyKind(k.LastUsedAt.Value, DateTimeKind.Utc))
                      : null,
        RevokedAt   = k.RevokedAt.HasValue
                      ? new DateTimeOffset(DateTime.SpecifyKind(k.RevokedAt.Value, DateTimeKind.Utc))
                      : null,
        IsActive    = k.IsActive,
        Scopes      = k.Scopes,
    };

    private static ApiKey ToDomain(ApiKeyPoco p) => new()
    {
        Id          = FromSurrealId(p.Id),
        AvatarId    = FromSurrealId(p.AvatarId),
        Name        = p.Name ?? string.Empty,
        KeyHash     = p.KeyHash ?? string.Empty,
        KeyPrefix   = p.KeyPrefix ?? string.Empty,
        CreatedDate = p.CreatedDate.UtcDateTime,
        ExpiresAt   = p.ExpiresAt?.UtcDateTime,
        LastUsedAt  = p.LastUsedAt?.UtcDateTime,
        RevokedAt   = p.RevokedAt?.UtcDateTime,
        IsActive    = p.IsActive,
        Scopes      = p.Scopes,
    };

    // ── POCO (private) ────────────────────────────────────────────────────────

    /// <summary>
    /// SurrealDB row shape for the <c>api_key</c> table. Private to this store
    /// because the source generator has not yet emitted a generated POCO for
    /// table 120. When it does, delete this type and substitute the generated
    /// one — no contract change.
    /// </summary>
    private sealed class ApiKeyPoco
    {
        [JsonPropertyName("id")]            public string Id          { get; set; } = string.Empty;
        [JsonPropertyName("avatar_id")]     public string AvatarId    { get; set; } = string.Empty;
        [JsonPropertyName("name")]          public string? Name       { get; set; }
        [JsonPropertyName("key_hash")]      public string KeyHash     { get; set; } = string.Empty;
        [JsonPropertyName("key_prefix")]    public string? KeyPrefix  { get; set; }
        [JsonPropertyName("created_date")]  public DateTimeOffset CreatedDate { get; set; }
        [JsonPropertyName("expires_at")]    public DateTimeOffset? ExpiresAt  { get; set; }
        [JsonPropertyName("last_used_at")]  public DateTimeOffset? LastUsedAt { get; set; }
        [JsonPropertyName("revoked_at")]    public DateTimeOffset? RevokedAt  { get; set; }
        [JsonPropertyName("is_active")]     public bool IsActive       { get; set; }
        [JsonPropertyName("scopes")]        public string? Scopes      { get; set; }
    }
}
