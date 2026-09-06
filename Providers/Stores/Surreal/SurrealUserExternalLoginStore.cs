// SPDX-License-Identifier: UNLICENSED
//
// ─── DI registration (Program.cs, alongside the other Surreal stores) ───
//
//   builder.Services.AddScoped<IUserExternalLoginStore, SurrealUserExternalLoginStore>();

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
/// SurrealDB-backed <see cref="IUserExternalLoginStore"/> (Phase 1B, D17/D18).
///
/// <para>Mirrors <see cref="SurrealKycIdentityStore"/>: the table name comes from a
/// compile-time constant, ids are bare hex in memory and <c>table:id</c> record links
/// on the wire, and every statement is parameterised.</para>
///
/// <para><b>The scope parameter is not optional anywhere.</b> Every WHERE clause in
/// this file leads with <c>tenant_scope = $_scope</c>. A lookup that omitted it would
/// resolve one tenant's Google account to another tenant's user, which is the
/// cross-tenant collapse D1/D23 exist to prevent.</para>
///
/// <para><b>There is no query on email anywhere in this file.</b> That absence is
/// load-bearing, not an oversight — see the class remarks on
/// <see cref="IUserExternalLoginStore"/>.</para>
/// </summary>
public sealed class SurrealUserExternalLoginStore : IUserExternalLoginStore
{
    private const string PublicStoreError =
        "EXTERNAL_LOGIN_STORE_UNAVAILABLE: Federated sign-in persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealUserExternalLoginStore> _logger;

    public SurrealUserExternalLoginStore(
        ISurrealExecutor executor,
        ILogger<SurrealUserExternalLoginStore>? logger = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? NullLogger<SurrealUserExternalLoginStore>.Instance;
    }

    public async Task<AZOAResult<UserExternalLogin?>> GetBySubjectAsync(
        string tenantScope,
        ExternalAuthProvider provider,
        string subject,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope) || string.IsNullOrWhiteSpace(subject))
            return AZOAResult<UserExternalLogin?>.Success(null, "No binding.");

        try
        {
            var query = SurrealQuery
                .Of("""
                    SELECT * FROM user_external_login
                    WHERE tenant_scope = $_scope
                      AND provider = $_provider
                      AND subject = $_subject
                    LIMIT 1
                    """)
                .WithParam("_scope", tenantScope)
                .WithParam("_provider", provider.ToString())
                .WithParam("_subject", subject);

            var row = await _executor.QuerySingleAsync<UserExternalLogin>(query, ct);
            return AZOAResult<UserExternalLogin?>.Success(
                row is null ? null : FromStorage(row), "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<UserExternalLogin?>(exception, nameof(GetBySubjectAsync), tenantScope);
        }
    }

    public async Task<AZOAResult<UserExternalLogin>> CreateAsync(
        UserExternalLogin binding,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try
        {
            var body = ToStorage(binding);

            // CREATE, never UPSERT. An UPSERT here would silently re-point an existing
            // binding at a different user, which is precisely the account-takeover
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
                        user_id = $_user,
                        email_at_link = $_email,
                        display_name_at_link = $_display_name,
                        last_login_at = $_last_login
                    RETURN AFTER
                    """)
                .WithParam("_t", UserExternalLogin.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_scope", body.TenantScope)
                .WithParam("_tenant", SurrealRecordParam.Of("user", binding.TenantId))
                .WithParam("_provider", body.Provider.ToString())
                .WithParam("_subject", body.Subject)
                .WithParam("_user", SurrealRecordParam.Of("user", binding.UserId))
                .WithParam("_email", body.EmailAtLink)
                .WithParam("_display_name", body.DisplayNameAtLink)
                .WithParam("_last_login", body.LastLoginAt);

            var response = await _executor.ExecuteAsync(query, ct);
            if (!response[0].IsOk)
            {
                return AZOAResult<UserExternalLogin>.FailureWithCode(
                    "This provider account is already linked to a user on this node.",
                    AzoaErrorCodes.Conflict);
            }

            var saved = response.GetValues<UserExternalLogin>(0).FirstOrDefault();
            return AZOAResult<UserExternalLogin>.Success(
                saved is null ? binding : FromStorage(saved), "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<UserExternalLogin>(exception, nameof(CreateAsync), binding.TenantScope);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<UserExternalLogin>>> GetByUserAsync(
        string userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return AZOAResult<IReadOnlyList<UserExternalLogin>>.Success([], "Success");

        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM user_external_login WHERE user_id = $_user")
                .WithParam("_user", SurrealRecordParam.Of("user", userId));

            var rows = await _executor.QueryAsync<UserExternalLogin>(query, ct);
            return AZOAResult<IReadOnlyList<UserExternalLogin>>.Success(
                rows.Select(FromStorage).ToList(), "Success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<UserExternalLogin>>(
                exception, nameof(GetByUserAsync), userId);
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
                .WithParam("_t", UserExternalLogin.SchemaNameConst)
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
        string userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope) || string.IsNullOrWhiteSpace(userId))
            return AZOAResult<bool>.Success(false, "Nothing to unlink.");

        try
        {
            // Filtered by the OWNING user as well as the scope: an unlink can only
            // ever remove the caller's own binding (the STARODK IDOR precedent —
            // route/body identity never widens the authenticated identity).
            //
            // DELETE removes this row and nothing else. There is no cascade to user:
            // the account survives an unlink and stays reachable through its other
            // credentials.
            var query = SurrealQuery
                .Of("""
                    DELETE FROM user_external_login
                    WHERE tenant_scope = $_scope
                      AND provider = $_provider
                      AND user_id = $_user
                    RETURN BEFORE
                    """)
                .WithParam("_scope", tenantScope)
                .WithParam("_provider", provider.ToString())
                .WithParam("_user", SurrealRecordParam.Of("user", userId));

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
        // entityId is a scope or a user id, never a provider subject: a subject is
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

    private static UserExternalLogin ToStorage(UserExternalLogin binding) => new()
    {
        Id = binding.Id,
        TenantScope = binding.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(binding.TenantId)
            ? null
            : SurrealLink.ToLink("user", binding.TenantId),
        Provider = binding.Provider,
        Subject = binding.Subject,
        UserId = SurrealLink.ToLink("user", binding.UserId) ?? string.Empty,
        EmailAtLink = binding.EmailAtLink,
        DisplayNameAtLink = binding.DisplayNameAtLink,
        LinkedAt = binding.LinkedAt.ToUniversalTime(),
        LastLoginAt = binding.LastLoginAt?.ToUniversalTime(),
    };

    private static UserExternalLogin FromStorage(UserExternalLogin binding) => new()
    {
        Id = SurrealLink.FromLink(binding.Id) ?? string.Empty,
        TenantScope = binding.TenantScope,
        TenantId = string.IsNullOrWhiteSpace(binding.TenantId)
            ? null
            : SurrealLink.FromLink(binding.TenantId),
        Provider = binding.Provider,
        Subject = binding.Subject,
        UserId = SurrealLink.FromLink(binding.UserId) ?? string.Empty,
        EmailAtLink = binding.EmailAtLink,
        DisplayNameAtLink = binding.DisplayNameAtLink,
        LinkedAt = binding.LinkedAt,
        LastLoginAt = binding.LastLoginAt,
    };
}
