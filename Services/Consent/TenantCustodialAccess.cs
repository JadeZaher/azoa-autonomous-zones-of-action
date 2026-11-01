using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Consent;

/// <inheritdoc/>
public sealed class TenantCustodialAccess(ISurrealExecutor executor) : ITenantCustodialAccess
{
    /// <inheritdoc/>
    public Task<TenantCustodialWitness?> CaptureAsync(Guid userId, string externalUserId, SigningConsentWitness authority,
        string requiredScope, CancellationToken ct = default)
        => TenantCustodialAuthorityFence.CaptureAsync(executor, userId, externalUserId, authority, requiredScope, ct);

    /// <inheritdoc/>
    public async Task<bool> ValidateAsync(TenantCustodialWitness witness, string requiredScope, CancellationToken ct = default)
    {
        var guards = TenantCustodialAuthorityFence.Create(witness, requiredScope);
        if (guards is null) return false;
        // raw: response authority linearizes after data collection against claim, offboarding and credential revocation.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(TenantCustodialAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        { return false; }
    }

    /// <inheritdoc/>
    public Task<TenantCustodialKind?> ResolveOwnershipAsync(Guid userId, string externalUserId, Guid tenantId, CancellationToken ct = default)
        => TenantCustodialAuthorityFence.OwnershipAsync(executor, userId, externalUserId, tenantId, ct);

    /// <inheritdoc/>
    public async Task<TenantClaimedConsent?> ReadClaimedConsentAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty || tenantId == Guid.Empty) return null;
        var grant = await TenantCustodialAuthorityFence.FirstLiveClaimedGrantAsync(executor, userId, tenantId, ct);
        return grant is null ? null : new TenantClaimedConsent(ConsentScopeCoverage.SplitScopes(grant.Scopes), grant.ExpiresAt);
    }
}
