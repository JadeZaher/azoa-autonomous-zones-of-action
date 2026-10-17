using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Persistence boundary for <see cref="IUser"/> aggregates.</summary>
public interface IUserStore
{
    /// <summary>Loads a single user by id.</summary>
    Task<AZOAResult<IUser>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads every user.</summary>
    Task<AZOAResult<IEnumerable<IUser>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates a user.</summary>
    Task<AZOAResult<IUser>> UpsertAsync(IUser user, CancellationToken ct = default);

    /// <summary>
    /// Creates a deterministic onboarding user without ever updating an
    /// existing row. A concurrent duplicate returns the authoritative row.
    /// </summary>
    Task<AZOAResult<IUser>> CreateIfAbsentAsync(IUser user, CancellationToken ct = default);

    /// <summary>Deletes a user by id.</summary>
    Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Lists every child user owned by the given tenant principal (matched on
    /// <c>owner_tenant_id</c>). Owner-scoped: returns ONLY that tenant's rows —
    /// never another tenant's children. Mirrors the
    /// <see cref="IApiKeyStore.ListByUserAsync"/> owner-scoped query.
    /// </summary>
    Task<AZOAResult<IEnumerable<IUser>>> ListByOwnerTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a single child user by the tenant's own external user id,
    /// scoped to that tenant (<c>owner_tenant_id = tenant AND external_user_id =
    /// externalUserId</c>). Returns <c>Result == null</c> with no error when no
    /// match exists — the manager interprets that as "create new" (idempotency),
    /// mirroring <see cref="IActionGraphBundleStore.GetByNameAndUserAsync"/>.
    /// </summary>
    Task<AZOAResult<IUser>> GetByTenantAndExternalUserAsync(Guid tenantId, string externalUserId, CancellationToken ct = default);

    /// <summary>
    /// user-sovereign-identity AC2: resolves the user bound to EXACTLY this
    /// wallet-auth <c>(address, chainType)</c> pair, or <c>Result == null</c> with no
    /// error when none exists (the manager creates a new self-owned user). The
    /// match is on the wallet binding ONLY — NEVER email/username/external_user_id —
    /// so an unauthenticated wallet-verify can never take over an account created by
    /// another auth method (AC2b).
    /// </summary>
    Task<AZOAResult<IUser>> GetByAuthWalletAsync(string address, string chainType, CancellationToken ct = default);

    /// <summary>CAS-updates wallet credentials without changing ownership.</summary>
    Task<AZOAResult<IUser>> TryUpdateAuthenticationAsync(IUser user, string? expectedAddress, string? expectedChain,
        Guid? expectedTenant, DateTime? expectedAuthNotBefore, CancellationToken ct = default);

    /// <summary>Atomically consumes the exact live invite, changes ownership and credentials, and records consent revocations.</summary>
    Task<AZOAResult<IUser>> TryClaimAuthenticationAsync(IUser user, string? expectedAddress, string? expectedChain,
        Guid expectedTenant, DateTime? expectedAuthNotBefore, AZOA.WebAPI.Models.WalletAuthClaimToken invite,
        CancellationToken ct = default);

    /// <summary>Advances the token revocation watermark without overwriting credentials or moving it backwards.</summary>
    Task<AZOAResult<bool>> AdvanceAuthenticationWatermarkAsync(Guid userId, DateTime notBefore, CancellationToken ct = default);

    /// <summary>Assigns a validated role only while the observed role remains current.</summary>
    Task<AZOAResult<IUser>> TryAssignDappRoleAsync(Guid userId, string expectedRole, string role, CancellationToken ct = default);

    /// <summary>Changes account activation only while the observed active state remains current.</summary>
    Task<AZOAResult<IUser>> TrySetActiveAsync(Guid userId, bool expectedActive, bool active, CancellationToken ct = default);

    /// <summary>Returns a bounded page of active principals with a live tenant-provision key.</summary>
    Task<AZOAResult<IReadOnlyList<IUser>>> ListTenantPrincipalsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default);
}
