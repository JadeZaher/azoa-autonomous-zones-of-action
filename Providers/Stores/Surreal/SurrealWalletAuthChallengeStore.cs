// ─── DI registration (orchestrator applies to Program.cs — do NOT edit here) ───
//
//   builder.Services.AddScoped<IWalletAuthChallengeStore, SurrealWalletAuthChallengeStore>();
//
// Scoped lifetime matches the other Surreal stores (e.g. SurrealApiKeyStore /
// SurrealConsentGrantStore). Register alongside them in the Surreal store block.

using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IWalletAuthChallengeStore"/> (user-sovereign-identity §1).
/// Mirrors the inline-POCO pattern of <see cref="SurrealApiKeyStore"/> /
/// <see cref="SurrealConsentGrantStore"/>: Guid("N") lowercase-hex record ids and a
/// private row POCO with <see cref="JsonPropertyNameAttribute"/> snake_case columns.
///
/// <para><see cref="TryConsumeAsync"/> is the no-TOCTOU single-use primitive: a single
/// conditional UPDATE filtered on <c>consumed_at = NONE AND expires_at &gt; now</c>, so
/// SurrealDB serializes two concurrent verifies and exactly one observes
/// <c>AffectedCount() == 1</c>.</para>
/// </summary>
public sealed class SurrealWalletAuthChallengeStore : IWalletAuthChallengeStore
{
    private const string Table = "wallet_auth_challenge";

    private readonly ISurrealExecutor _executor;

    public SurrealWalletAuthChallengeStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<WalletAuthChallenge>> CreateAsync(WalletAuthChallenge challenge, CancellationToken ct = default)
    {
        try
        {
            if (challenge.Id == Guid.Empty) challenge.Id = Guid.NewGuid();
            var poco = FromDomain(challenge);

            var q = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", Table)
                .WithParam("_id", poco.Id)
                .WithParam("_body", poco);

            var response = await _executor.ExecuteAsync(q, ct);
            if (!response[0].IsOk)
            {
                // Surface the DB error (notably a nonce UNIQUE collision) as a
                // store-prefixed domain error, never the raw transport exception.
                return new AZOAResult<WalletAuthChallenge>
                {
                    IsError = true,
                    Message = $"SurrealWalletAuthChallengeStore.CreateAsync failed: {response[0].ErrorText}"
                };
            }

            var saved = response.GetValues<WalletAuthChallengePoco>(0).FirstOrDefault();
            return new AZOAResult<WalletAuthChallenge>
            {
                Result = saved is not null ? ToDomain(saved) : challenge,
                Message = "Saved."
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletAuthChallenge>().CaptureException(ex, $"SurrealWalletAuthChallengeStore.CreateAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<WalletAuthChallenge>> GetByNonceAsync(string nonce, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(nonce))
                return new AZOAResult<WalletAuthChallenge> { Result = null, Message = "No nonce supplied." };

            var q = SurrealQuery
                .Of("SELECT * FROM wallet_auth_challenge WHERE nonce = $_nonce LIMIT 1")
                .WithParam("_nonce", nonce);

            var row = await _executor.QuerySingleAsync<WalletAuthChallengePoco>(q, ct);
            return new AZOAResult<WalletAuthChallenge>
            {
                Result = row is null ? null : ToDomain(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletAuthChallenge>().CaptureException(ex, $"SurrealWalletAuthChallengeStore.GetByNonceAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<WalletAuthChallenge>> GetLatestLiveByAddressAsync(
        string address, string chainType, DateTime now, CancellationToken ct = default)
    {
        try
        {
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            var q = SurrealQuery
                .Of(@"SELECT * FROM wallet_auth_challenge
                      WHERE address = $_addr
                        AND chain_type = $_chain
                        AND consumed_at = NONE
                        AND expires_at > $_now
                      ORDER BY created_at DESC
                      LIMIT 1")
                .WithParam("_addr", address)
                .WithParam("_chain", chainType)
                .WithParam("_now", nowUtc);

            var row = await _executor.QuerySingleAsync<WalletAuthChallengePoco>(q, ct);
            return new AZOAResult<WalletAuthChallenge>
            {
                Result = row is null ? null : ToDomain(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<WalletAuthChallenge>().CaptureException(ex, $"SurrealWalletAuthChallengeStore.GetLatestLiveByAddressAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> TryConsumeAsync(string nonce, DateTime now, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(nonce))
                return new AZOAResult<bool> { Result = false, Message = "No nonce supplied." };

            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);

            // ATOMIC single-use consume (AC1). The conditional WHERE is the no-TOCTOU
            // gate: SurrealDB serializes two concurrent verifies and exactly one row
            // transitions consumed_at NONE → now. AffectedCount() == 1 ⇒ this caller won.
            var q = SurrealQuery
                .Of(@"UPDATE wallet_auth_challenge
                      SET consumed_at = $_now
                      WHERE nonce = $_nonce
                        AND consumed_at = NONE
                        AND expires_at > $_now
                      RETURN BEFORE")
                .WithParam("_nonce", nonce)
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
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealWalletAuthChallengeStore.TryConsumeAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<int>> CountLiveByAddressAsync(
        string address, string chainType, DateTime now, CancellationToken ct = default)
    {
        try
        {
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            var q = SurrealQuery
                .Of(@"SELECT * FROM wallet_auth_challenge
                      WHERE address = $_addr
                        AND chain_type = $_chain
                        AND consumed_at = NONE
                        AND expires_at > $_now")
                .WithParam("_addr", address)
                .WithParam("_chain", chainType)
                .WithParam("_now", nowUtc);

            var rows = await _executor.QueryAsync<WalletAuthChallengePoco>(q, ct);
            return new AZOAResult<int> { Result = rows.Count, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<int>().CaptureException(ex, $"SurrealWalletAuthChallengeStore.CountLiveByAddressAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────


    private static WalletAuthChallengePoco FromDomain(WalletAuthChallenge c) => new()
    {
        Id            = SurrealId.ToSurrealId(c.Id),
        Nonce         = c.Nonce,
        Address       = c.Address,
        ChainType     = c.ChainType,
        DomainMessage = c.DomainMessage,
        ExpiresAt     = new DateTimeOffset(DateTime.SpecifyKind(c.ExpiresAt, DateTimeKind.Utc)),
        ConsumedAt    = c.ConsumedAt.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(c.ConsumedAt.Value, DateTimeKind.Utc))
                        : null,
        CreatedAt     = new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)),
    };

    private static WalletAuthChallenge ToDomain(WalletAuthChallengePoco p) => new()
    {
        Id            = SurrealId.FromSurrealId(p.Id),
        Nonce         = p.Nonce,
        Address       = p.Address,
        ChainType     = p.ChainType,
        DomainMessage = p.DomainMessage,
        ExpiresAt     = p.ExpiresAt.UtcDateTime,
        ConsumedAt    = p.ConsumedAt?.UtcDateTime,
        CreatedAt     = p.CreatedAt.UtcDateTime,
    };

    // ── POCO (private; inline until source-gen catches up) ────────────────────

    private sealed class WalletAuthChallengePoco : Azoa.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]             public string Id            { get; set; } = string.Empty;
        [JsonPropertyName("nonce")]          public string Nonce         { get; set; } = string.Empty;
        [JsonPropertyName("address")]        public string Address       { get; set; } = string.Empty;
        [JsonPropertyName("chain_type")]     public string ChainType     { get; set; } = string.Empty;
        [JsonPropertyName("domain_message")] public string DomainMessage { get; set; } = string.Empty;
        [JsonPropertyName("expires_at")]     public DateTimeOffset ExpiresAt   { get; set; }
        [JsonPropertyName("consumed_at")]    public DateTimeOffset? ConsumedAt { get; set; }
        [JsonPropertyName("created_at")]     public DateTimeOffset CreatedAt   { get; set; }
    }
}
