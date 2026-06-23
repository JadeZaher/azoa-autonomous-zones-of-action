// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddScoped<IWalletAuthClaimTokenStore, SurrealWalletAuthClaimTokenStore>();
//
// Scoped lifetime matches the other Surreal stores. Register alongside
// SurrealWalletAuthChallengeStore in the Surreal store block.

using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IWalletAuthClaimTokenStore"/> (user-sovereign-identity §2).
/// Mirrors <see cref="SurrealWalletAuthChallengeStore"/>: Guid("N") record ids, record-link
/// columns for target/tenant, and the atomic <see cref="TryConsumeAsync"/> conditional UPDATE.
/// </summary>
public sealed class SurrealWalletAuthClaimTokenStore : IWalletAuthClaimTokenStore
{
    private const string Table = "wallet_auth_claim_token";

    private readonly ISurrealExecutor _executor;

    public SurrealWalletAuthClaimTokenStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<WalletAuthClaimToken>> CreateAsync(WalletAuthClaimToken token, CancellationToken ct = default)
    {
        try
        {
            if (token.Id == Guid.Empty) token.Id = Guid.NewGuid();
            var poco = FromDomain(token);

            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", poco.Id)
                .WithParam("_body", poco);

            var response = await _executor.ExecuteAsync(q, ct);
            if (!response[0].IsOk)
            {
                return new AZOAResult<WalletAuthClaimToken>
                {
                    IsError = true,
                    Message = $"SurrealWalletAuthClaimTokenStore.CreateAsync failed: {response[0].ErrorText}"
                };
            }

            var saved = response.GetValues<WalletAuthClaimTokenPoco>(0).FirstOrDefault();
            return new AZOAResult<WalletAuthClaimToken>
            {
                Result = saved is not null ? ToDomain(saved) : token,
                Message = "Saved."
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletAuthClaimToken>().CaptureException(ex, $"SurrealWalletAuthClaimTokenStore.CreateAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<WalletAuthClaimToken>> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
                return new AZOAResult<WalletAuthClaimToken> { Result = null, Message = "No token supplied." };

            var q = SurrealQuery
                .Of("SELECT * FROM wallet_auth_claim_token WHERE token = $_token LIMIT 1")
                .WithParam("_token", token);

            var row = await _executor.QuerySingleAsync<WalletAuthClaimTokenPoco>(q, ct);
            return new AZOAResult<WalletAuthClaimToken>
            {
                Result = row is null ? null : ToDomain(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletAuthClaimToken>().CaptureException(ex, $"SurrealWalletAuthClaimTokenStore.GetByTokenAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> TryConsumeAsync(string token, DateTime now, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
                return new AZOAResult<bool> { Result = false, Message = "No token supplied." };

            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);

            // ATOMIC single-use redeem — same no-TOCTOU gate as the auth nonce.
            var q = SurrealQuery
                .Of(@"UPDATE wallet_auth_claim_token
                      SET consumed_at = $_now
                      WHERE token = $_token
                        AND consumed_at = NONE
                        AND expires_at > $_now
                      RETURN BEFORE")
                .WithParam("_token", token)
                .WithParam("_now", nowUtc);

            var response = await _executor.ExecuteAsync(q, ct);
            response.EnsureAllOk();
            return new AZOAResult<bool>
            {
                Result = response[0].AffectedCount() == 1,
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealWalletAuthClaimTokenStore.TryConsumeAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id) => id.ToString("N").ToLowerInvariant();
    private static Guid FromSurrealId(string id) => Guid.ParseExact(id, "N");

    private static WalletAuthClaimTokenPoco FromDomain(WalletAuthClaimToken t) => new()
    {
        Id             = ToSurrealId(t.Id),
        Token          = t.Token,
        TargetAvatarId = SurrealLink.ToLink("avatar", ToSurrealId(t.TargetAvatarId)) ?? string.Empty,
        TenantId       = SurrealLink.ToLink("avatar", ToSurrealId(t.TenantId)) ?? string.Empty,
        ExpiresAt      = new DateTimeOffset(DateTime.SpecifyKind(t.ExpiresAt, DateTimeKind.Utc)),
        ConsumedAt     = t.ConsumedAt.HasValue
                         ? new DateTimeOffset(DateTime.SpecifyKind(t.ConsumedAt.Value, DateTimeKind.Utc))
                         : null,
        CreatedAt      = new DateTimeOffset(DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)),
    };

    private static WalletAuthClaimToken ToDomain(WalletAuthClaimTokenPoco p) => new()
    {
        Id             = FromSurrealId(p.Id),
        Token          = p.Token,
        TargetAvatarId = FromSurrealId(SurrealLink.FromLink(p.TargetAvatarId)!),
        TenantId       = FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        ExpiresAt      = p.ExpiresAt.UtcDateTime,
        ConsumedAt     = p.ConsumedAt?.UtcDateTime,
        CreatedAt      = p.CreatedAt.UtcDateTime,
    };

    // ── POCO (private; inline until source-gen catches up) ────────────────────

    private sealed class WalletAuthClaimTokenPoco : Azoa.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]               public string Id             { get; set; } = string.Empty;
        [JsonPropertyName("token")]            public string Token          { get; set; } = string.Empty;
        [JsonPropertyName("target_avatar_id")] public string TargetAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("tenant_id")]        public string TenantId       { get; set; } = string.Empty;
        [JsonPropertyName("expires_at")]       public DateTimeOffset ExpiresAt   { get; set; }
        [JsonPropertyName("consumed_at")]      public DateTimeOffset? ConsumedAt { get; set; }
        [JsonPropertyName("created_at")]       public DateTimeOffset CreatedAt   { get; set; }
    }
}
