// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

// AZOA.WebAPI.Helpers also declares an Encoding (base32/base64 chain helpers), so the
// bare name is ambiguous here. Every use below is UTF-8 byte counting/hashing.
using Encoding = System.Text.Encoding;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>
/// The implied, revocable consent grant a TENANT-PROVISIONED user carries from the
/// moment <c>TenantManager.ProvisionChildAsync</c> creates them.
///
/// <para><b>Why it exists.</b> A custodially onboarded person never logs into AZOA —
/// they exist because a tenant onboarded them. Without a grant issued at provisioning
/// time there are only two outcomes, both wrong: the person must log into AZOA to
/// click "allow" (impossible — they hold no AZOA credential), or tenant-key operations
/// bypass consent entirely. The implied grant is the third way: a real, durable,
/// audited, user-revocable <see cref="ConsentGrant"/> that exists from birth and can be
/// withdrawn at any moment.</para>
///
/// <para><b>Why it is a <see cref="GrantOrigin.Participation"/> grant.</b> Participation
/// is the crypto-free, join-driven standing grant, and <c>ConsentManager.GrantAsync</c>
/// REJECTS any <see cref="AzoaScopes.ValueSigningScopes"/> member under that origin
/// (H4). So the implied grant is structurally incapable of authorising an outbound
/// value signature — a swap, a transfer, a token create or a credential clawback each
/// still need a deliberate <c>UserExplicit</c> grant made by the person themselves.</para>
/// </summary>
public static class TenantProvisionedGrant
{
    /// <summary>Namespaced, versioned prefix of the deterministic participation reference.</summary>
    public const string ParticipationRefPrefix = "tenant-provisioned:v1:";

    /// <summary>
    /// The scope ceiling of the implied grant — the minimum a tenant needs to run a
    /// custodial onboarding end to end, and nothing that spends the person's holdings.
    ///
    /// <list type="bullet">
    /// <item><c>wallet:manage</c> — materialise the custodial wallet an allocation lands in.</item>
    /// <item><c>kyc:read</c> / <c>kyc:submit</c> — run identity verification on their behalf.</item>
    /// <item><c>action:execute</c> — non-value participation steps.</item>
    /// <item><c>nft:mint</c> — the INBOUND credit: allocating units INTO the person's
    ///   account. It adds to their balance and spends nothing of theirs, which is why it
    ///   is not a value-signing scope; it is the scope <c>AllocationManager</c> stamps on
    ///   an <c>AllocationKind.Mint</c> operation.</item>
    /// </list>
    ///
    /// Every member of <see cref="AzoaScopes.ValueSigningScopes"/> is deliberately absent.
    /// <see cref="CarriesNoValueSigningScope"/> states it; <c>ConsentManager.GrantAsync</c>
    /// enforces it and would reject this set at runtime if it ever drifted.
    /// </summary>
    public static readonly IReadOnlyList<string> ImpliedScopes = new[]
    {
        AzoaScopes.WalletManage,
        AzoaScopes.KycRead,
        AzoaScopes.KycSubmit,
        AzoaScopes.ActionExecute,
        AzoaScopes.NftMint,
    };

    /// <summary>True iff <see cref="ImpliedScopes"/> smuggles in no outbound value-signing scope (H4).</summary>
    public static bool CarriesNoValueSigningScope
        => !ImpliedScopes.Any(AzoaScopes.ValueSigningScopes.Contains);

    /// <summary>
    /// The deterministic participation reference for a (tenant, external user) pair — the
    /// handle the tenant offboards by, and the key that makes issuance idempotent. Derived
    /// rather than stored so the provisioning path and the offboard path compute the same
    /// value without a lookup, and hashed so a tenant's own external user id never becomes
    /// a URL path segment.
    /// </summary>
    public static string ParticipationRefFor(Guid tenantId, string externalUserId)
    {
        var normalized = (externalUserId ?? string.Empty).Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"azoa:tenant-provisioned-consent:v1:{tenantId:N}:{normalized}"));
        return ParticipationRefPrefix + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}

/// <summary>
/// Issues the implied participation grant for a newly provisioned custodial user. One
/// method, one rule: create the grant exactly once per (tenant, external user), never again.
/// </summary>
public interface ITenantProvisionedGrantIssuer
{
    /// <summary>
    /// Issues the implied grant from <paramref name="provisionedUserId"/> to
    /// <paramref name="tenantId"/>, or returns the existing record when this pair has ever
    /// had one. A REVOKED prior grant is returned as-is and never re-issued — re-provisioning
    /// must not resurrect authority the person withdrew.
    /// </summary>
    Task<AZOAResult<ConsentGrant>> IssueOnProvisionAsync(
        Guid tenantId, Guid provisionedUserId, string externalUserId, CancellationToken ct = default);
}

/// <inheritdoc cref="ITenantProvisionedGrantIssuer" />
public sealed class TenantProvisionedGrantIssuer : ITenantProvisionedGrantIssuer
{
    private readonly IConsentManager _consent;
    private readonly IConsentGrantStore _grants;
    private readonly ILogger<TenantProvisionedGrantIssuer> _logger;

    public TenantProvisionedGrantIssuer(
        IConsentManager consent,
        IConsentGrantStore grants,
        ILogger<TenantProvisionedGrantIssuer>? logger = null)
    {
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _logger = logger ?? NullLogger<TenantProvisionedGrantIssuer>.Instance;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<ConsentGrant>> IssueOnProvisionAsync(
        Guid tenantId, Guid provisionedUserId, string externalUserId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || provisionedUserId == Guid.Empty)
            return AZOAResult<ConsentGrant>.Failure(
                "An implied consent grant needs both a tenant and a provisioned user.");

        var participationRef = TenantProvisionedGrant.ParticipationRefFor(tenantId, externalUserId);

        // Idempotency AND revocation durability in one read. The store query is scoped to
        // (tenant, ref), so any prior record can only be this tenant's own. ANY prior
        // record — live or revoked — suppresses issuance. The only state this heals over
        // is a (tenant, user) pair that has never had a grant at all, i.e. a crash between
        // the user create and the grant write; it can never re-mint authority a person revoked.
        var prior = await _grants.ListByTenantAndParticipationRefAsync(tenantId, participationRef, ct);
        if (prior.IsError)
        {
            // Fail closed: an unreadable grant history must never become a second grant.
            _logger.LogWarning(
                "Implied consent grant lookup failed for tenant {Tenant} ref {Ref}: {Message}",
                tenantId, participationRef, prior.Message);
            return AZOAResult<ConsentGrant>.FailureWithCode(
                "Consent history could not be read; no implied grant was issued.",
                AzoaErrorCodes.DependencyUnavailable);
        }

        var existing = (prior.Result ?? Enumerable.Empty<ConsentGrant>())
            .FirstOrDefault(g => g.GrantorUserId == provisionedUserId);
        if (existing is not null)
            return new AZOAResult<ConsentGrant>
            {
                Result = existing,
                Message = existing.RevokedAt is null
                    ? "Implied consent grant already exists."
                    : "Implied consent grant was revoked and is not re-issued.",
            };

        // GrantParticipationAsync is the single authority: it normalises the scope set,
        // enforces the H4 value-scope exclusion, writes the immutable audit row and
        // enqueues the consent.granted webhook. None of that is duplicated here.
        return await _consent.GrantParticipationAsync(
            provisionedUserId, tenantId, participationRef, TenantProvisionedGrant.ImpliedScopes, ct);
    }
}

/// <summary>DI wiring for the implied tenant-provisioned consent grant; called from <c>Program.cs</c>.</summary>
public static class TenantProvisionedGrantRegistration
{
    /// <summary>
    /// Registers <see cref="ITenantProvisionedGrantIssuer"/>. Throws at startup when the
    /// consent authority or grant store it depends on is absent, so a half-wired container
    /// fails loudly at boot rather than quietly provisioning users no tenant can act for.
    /// </summary>
    public static IServiceCollection AddTenantProvisionedConsentGrants(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.All(descriptor => descriptor.ServiceType != typeof(IConsentManager)))
            throw new InvalidOperationException(
                "AddTenantProvisionedConsentGrants requires IConsentManager to be registered first.");
        if (services.All(descriptor => descriptor.ServiceType != typeof(IConsentGrantStore)))
            throw new InvalidOperationException(
                "AddTenantProvisionedConsentGrants requires IConsentGrantStore to be registered first.");

        services.AddScoped<ITenantProvisionedGrantIssuer, TenantProvisionedGrantIssuer>();
        return services;
    }
}
