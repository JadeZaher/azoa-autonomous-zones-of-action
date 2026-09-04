// SPDX-License-Identifier: UNLICENSED
//
// ─── DI registration (Program.cs, alongside the other Surreal stores) ───
//
//   builder.Services.AddScoped<IAvatarExternalLoginStore, SurrealAvatarExternalLoginStore>();

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IAvatarExternalLoginStore"/> (Phase 1B, D17/D18).
///
/// <para>Mirrors <see cref="SurrealKycIdentityStore"/>: the table name comes from a
/// compile-time constant, ids are bare hex in memory and <c>table:id</c> record links
/// on the wire, and every statement is parameterised.</para>
///
/// <para><b>The scope parameter is not optional anywhere.</b> Every WHERE clause in
/// this file leads with <c>tenant_scope = $_scope</c>. A lookup that omitted it would
/// resolve one tenant's Google account to another tenant's avatar, which is the
/// cross-tenant collapse D1/D23 exist to prevent.</para>
///
/// <para><b>There is no query on email anywhere in this file.</b> That absence is
/// load-bearing, not an oversight — see the class remarks on
/// <see cref="IAvatarExternalLoginStore"/>.</para>
/// </summary>
public sealed class SurrealAvatarExternalLoginStore : IAvatarExternalLoginStore
{
    private const string PublicStoreError =
        "EXTERNAL_LOGIN_STORE_UNAVAILABLE: Federated sign-in persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealAvatarExternalLoginStore> _logger;

    public SurrealAvatarExternalLoginStore(
        ISurrealExecutor executor,
        ILogger<SurrealAvatarExternalLoginStore>? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? NullLogger<SurrealAvatarExternalLoginStore>.Instance;
    }

    public async Task<AZOAResult<AvatarExternalLogin?>> GetBySubjectAsync(
        string tenantScope,
        ExternalAuthProvider provider,
        string subject,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope) || string.IsNullOrWhiteSpace(subject))
            return AZOAResult<AvatarExternalLogin?>.Success(null, "No binding.");

        try
        {
            var query = SurrealQuery
                .Of("""
                    SELECT * FROM avatar_external_login
                    WHERE tenant_scope = $_scope
                      AND provider = $_provider
                      AND subject = $_subject
                    LIMIT 1
                    """)
                .WithParam("_scope", tenantScope)
                .WithParam("_provider", provider.ToString())
                .WithParam("_subject", subject);

            var row = await _executor.QuerySingleAsync<AvatarExternalLogin>(query, ct);
            return AZOAResult<AvatarExternalLogin?>.Success(
                row is null ? null : FromStorage(row), "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<AvatarExternalLogin?>(exception, nameof(GetBySubjectAsync), tenantScope);
        }
    }

    public async Task<AZOAResult<AvatarExternalLogin>> CreateAsync(
        AvatarExternalLogin binding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            var body = ToStorage(binding);

            // CREATE, never UPSERT. An UPSERT here would silently re-point an existing
            // binding at a different avatar, which is precisely the account-takeover
            // shape this table exists to make impossible. A collision on the
            // (tenant_scope, provider, subject) UNIQUE index is a real conflict and is
            // surfaced as one.
            var query = SurrealQuery
                .Of("""
                    CREATE type::record($_t, $_id)
                    SET tenant_scope = $_scope,
                        tenant_id = $_tenant,
                        provider = $_provider,
                        subject = $_subject,
                        avatar_id = $_avatar,
                        email_at_link = $_email,
                        display_name_at_link = $_display_name,
                        last_login_at = $_last_login
                    RETURN AFTER
                    """)
                .WithParam("_t", AvatarExternalLogin.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_scope", body.TenantScope)
                .WithParam("_tenant", SurrealRecordParam.Of("avatar", binding.TenantId))
                .WithParam("_provider", body.Provider.ToString())
                .WithParam("_subject", body.Subject)
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", binding.AvatarId))
                .WithParam("_email", body.EmailAtLink)
                .WithParam("_display_name", body.DisplayNameAtLink)
                .WithParam("_last_login", body.LastLoginAt);

            var response = await _executor.ExecuteAsync(query, ct);
            if (!response[0].IsOk)
            {
                return AZOAResult<AvatarExternalLogin>.FailureWithCode(
                    "This provider account is already linked to an avatar on this node.",
                    AzoaErrorCodes.Conflict);
            }

            var saved = response.GetValues<AvatarExternalLogin>(0).FirstOrDefault();
            return AZOAResult<AvatarExternalLogin>.Success(
                saved is null ? binding : FromStorage(saved), "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<AvatarExternalLogin>(exception, nameof(CreateAsync), binding.TenantScope);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<AvatarExternalLogin>>> GetByAvatarAsync(
        string avatarId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
            return AZOAResult<IReadOnlyList<AvatarExternalLogin>>.Success([], "Success");

        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM avatar_external_login WHERE avatar_id = $_avatar")
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", avatarId));

            var rows = await _executor.QueryAsync<AvatarExternalLogin>(query, ct);
            return AZOAResult<IReadOnlyList<AvatarExternalLogin>>.Success(
                rows.Select(FromStorage).ToList(), "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<AvatarExternalLogin>>(
                exception, nameof(GetByAvatarAsync), avatarId);
        }
    }

    public async Task<AZOAResult<bool>> TouchLastLoginAsync(
        string id,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return AZOAResult<bool>.Success(false, "No binding id.");

        try
        {
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET last_login_at = $_at")
                .WithParam("_t", AvatarExternalLogin.SchemaNameConst)
                .WithParam("_id", id)
                .WithParam("_at", at.ToUniversalTime());

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(response[0].AffectedCount() == 1, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(TouchLastLoginAsync), id);
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(
        string tenantScope,
        ExternalAuthProvider provider,
        string avatarId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope) || string.IsNullOrWhiteSpace(avatarId))
            return AZOAResult<bool>.Success(false, "Nothing to unlink.");

        try
        {
            // Filtered by the OWNING avatar as well as the scope: an unlink can only
            // ever remove the caller's own binding (the STARODK IDOR precedent —
            // route/body identity never widens the authenticated identity).
            //
            // DELETE removes this row and nothing else. There is no cascade to avatar:
            // the account survives an unlink and stays reachable through its other
            // credentials.
            var query = SurrealQuery
                .Of("""
                    DELETE FROM avatar_external_login
                    WHERE tenant_scope = $_scope
                      AND provider = $_provider
                      AND avatar_id = $_avatar
                    RETURN BEFORE
                    """)
                .WithParam("_scope", tenantScope)
                .WithParam("_provider", provider.ToString())
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", avatarId));

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(response[0].AffectedCount() >= 1, "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(DeleteAsync), tenantScope);
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, string entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        // entityId is a scope or an avatar id, never a provider subject: a subject is
        // the federated account identifier and does not belong in logs.
        _logger.LogError(
            exception,
            "External login persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    // ── Mapping (bare hex in memory, record links on the wire) ────────────────

    private static AvatarExternalLogin ToStorage(AvatarExternalLogin binding) => new()
    {
        Id = binding.Id,
        TenantScope = binding.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(binding.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", binding.TenantId),
        Provider = binding.Provider,
        Subject = binding.Subject,
        AvatarId = SurrealLink.ToLink("avatar", binding.AvatarId) ?? string.Empty,
        EmailAtLink = binding.EmailAtLink,
        DisplayNameAtLink = binding.DisplayNameAtLink,
        LinkedAt = binding.LinkedAt.ToUniversalTime(),
        LastLoginAt = binding.LastLoginAt?.ToUniversalTime(),
    };

    private static AvatarExternalLogin FromStorage(AvatarExternalLogin binding) => new()
    {
        Id = SurrealLink.FromLink(binding.Id) ?? string.Empty,
        TenantScope = binding.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(binding.TenantId)
            ? null
            : SurrealLink.FromLink(binding.TenantId),
        Provider = binding.Provider,
        Subject = binding.Subject,
        AvatarId = SurrealLink.FromLink(binding.AvatarId) ?? string.Empty,
        EmailAtLink = binding.EmailAtLink,
        DisplayNameAtLink = binding.DisplayNameAtLink,
        LinkedAt = binding.LinkedAt,
        LastLoginAt = binding.LastLoginAt,
    };
}
