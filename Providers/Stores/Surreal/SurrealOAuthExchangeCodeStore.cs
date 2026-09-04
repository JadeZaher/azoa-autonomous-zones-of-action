// SPDX-License-Identifier: UNLICENSED
//
// ─── DI registration (Program.cs, alongside the other Surreal stores) ───
//
//   builder.Services.AddScoped<IOAuthExchangeCodeStore, SurrealOAuthExchangeCodeStore>();

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IOAuthExchangeCodeStore"/> (Phase 1B, D18).
///
/// <para><see cref="TryConsumeAsync"/> is the no-TOCTOU single-use primitive, copied
/// from <see cref="SurrealWalletAuthChallengeStore"/>: one conditional UPDATE filtered
/// on <c>consumed_at = NONE AND expires_at &gt; now</c>, so SurrealDB serialises two
/// concurrent redemptions and exactly one observes <c>AffectedCount() == 1</c>. A
/// read-then-write would let a replayed code mint two sessions.</para>
/// </summary>
public sealed class SurrealOAuthExchangeCodeStore : IOAuthExchangeCodeStore
{
    private const string PublicStoreError =
        "OAUTH_EXCHANGE_STORE_UNAVAILABLE: Federated sign-in persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealOAuthExchangeCodeStore> _logger;

    public SurrealOAuthExchangeCodeStore(
        ISurrealExecutor executor,
        ILogger<SurrealOAuthExchangeCodeStore>? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? NullLogger<SurrealOAuthExchangeCodeStore>.Instance;
    }

    public async Task<AZOAResult<OAuthExchangeCode>> CreateAsync(
        OAuthExchangeCode code,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        try
        {
            var body = ToStorage(code);

            var query = SurrealQuery
                .Of("""
                    CREATE type::record($_t, $_id)
                    SET code_hash = $_hash,
                        purpose = $_purpose,
                        avatar_id = $_avatar,
                        provider = $_provider,
                        tenant_scope = $_scope,
                        tenant_id = $_tenant,
                        return_to = $_return_to,
                        expires_at = $_expires,
                        consumed_at = NONE
                    RETURN AFTER
                    """)
                .WithParam("_t", OAuthExchangeCode.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_hash", body.CodeHash)
                .WithParam("_purpose", body.Purpose.ToString())
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", code.AvatarId))
                .WithParam("_provider", body.Provider.ToString())
                .WithParam("_scope", body.TenantScope)
                .WithParam("_tenant", SurrealRecordParam.Of("avatar", code.TenantId))
                .WithParam("_return_to", body.ReturnTo)
                .WithParam("_expires", body.ExpiresAt);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();

            var saved = response.GetValues<OAuthExchangeCode>(0).FirstOrDefault();
            return AZOAResult<OAuthExchangeCode>.Success(
                saved is null ? code : FromStorage(saved), "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<OAuthExchangeCode>(exception, nameof(CreateAsync), code.Id);
        }
    }

    public async Task<AZOAResult<OAuthExchangeCode?>> TryConsumeAsync(
        string codeHash,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codeHash))
            return AZOAResult<OAuthExchangeCode?>.Success(null, "No code supplied.");

        try
        {
            var nowUtc = now.ToUniversalTime();

            // RETURN BEFORE hands back the row as it was at the instant this caller won
            // the race, so the winner gets the payload and the loser gets nothing.
            var query = SurrealQuery
                .Of("""
                    UPDATE oauth_exchange_code
                    SET consumed_at = $_now
                    WHERE code_hash = $_hash
                      AND consumed_at = NONE
                      AND expires_at > $_now
                    RETURN BEFORE
                    """)
                .WithParam("_hash", codeHash)
                .WithParam("_now", nowUtc);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();

            if (response[0].AffectedCount() != 1)
            {
                // Unknown, already consumed, and expired are ONE answer on purpose:
                // distinguishing them tells an attacker which codes ever existed.
                return AZOAResult<OAuthExchangeCode?>.Success(null, "No live code.");
            }

            var consumed = response.GetValues<OAuthExchangeCode>(0).FirstOrDefault();
            return AZOAResult<OAuthExchangeCode?>.Success(
                consumed is null ? null : FromStorage(consumed), "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The hash is not logged: it is the stored form of a live credential.
            return StoreFailure<OAuthExchangeCode?>(exception, nameof(TryConsumeAsync), "(redacted)");
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, string entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "OAuth exchange-code persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static OAuthExchangeCode ToStorage(OAuthExchangeCode code) => new()
    {
        Id = code.Id,
        CodeHash = code.CodeHash,
        Purpose = code.Purpose,
        AvatarId = SurrealLink.ToLink("avatar", code.AvatarId) ?? string.Empty,
        Provider = code.Provider,
        TenantScope = code.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(code.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", code.TenantId),
        ReturnTo = code.ReturnTo,
        ExpiresAt = code.ExpiresAt.ToUniversalTime(),
        ConsumedAt = code.ConsumedAt?.ToUniversalTime(),
        CreatedAt = code.CreatedAt.ToUniversalTime(),
    };

    private static OAuthExchangeCode FromStorage(OAuthExchangeCode code) => new()
    {
        Id = SurrealLink.FromLink(code.Id) ?? string.Empty,
        CodeHash = code.CodeHash,
        Purpose = code.Purpose,
        AvatarId = SurrealLink.FromLink(code.AvatarId) ?? string.Empty,
        Provider = code.Provider,
        TenantScope = code.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(code.TenantId)
            ? null
            : SurrealLink.FromLink(code.TenantId),
        ReturnTo = code.ReturnTo,
        ExpiresAt = code.ExpiresAt,
        ConsumedAt = code.ConsumedAt,
        CreatedAt = code.CreatedAt,
    };
}
