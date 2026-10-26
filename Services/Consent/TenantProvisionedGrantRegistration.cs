// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Models.Responses;

using Encoding = System.Text.Encoding;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>Provisioning participation scope and stable membership identity; see AGENTS.md.</summary>
public static class TenantProvisionedGrant
{
    /// <summary>Namespaced, versioned prefix of the deterministic participation reference.</summary>
    public const string ParticipationRefPrefix = ConsentScopeCoverage.TenantProvisionedRefPrefix;

    /// <summary>Stored non-spending provisioning scopes; the reserve-exchange scope is covered at check time (Core/AGENTS.md §consent-scope-coverage).</summary>
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

    /// <summary>Derives the stable tenant-scoped membership reference.</summary>
    public static string ParticipationRefFor(Guid tenantId, string externalUserId)
    {
        var normalized = (externalUserId ?? string.Empty).Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"azoa:tenant-provisioned-consent:v1:{tenantId:N}:{normalized}"));
        return ParticipationRefPrefix + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>Names the original tenant-authorized enrollment command independently of later direct grants.</summary>
    public static Guid RequestIdFor(Guid tenantId, Guid grantorId, string externalUserId)
        => Guid.ParseExact(CanonicalJson.Hash(new { Domain = "consent-provision-v1", TenantId = tenantId,
            Grantor = grantorId, External = externalUserId.Trim() })[..32], "N");

    /// <summary>Names the original immutable enrollment receipt.</summary>
    public static string CommandIdFor(Guid tenantId, Guid grantorId, string externalUserId)
        => ConsentLifecycleIdentity.CommandId(grantorId, RequestIdFor(tenantId, grantorId, externalUserId));
}

/// <summary>Issues an immutable provisioning grant under the actual tenant credential.</summary>
public interface ITenantProvisionedGrantIssuer
{
    /// <summary>Replays the original command receipt without recreating revoked authority.</summary>
    Task<AZOAResult<ConsentLifecycleReceipt>> IssueOnProvisionAsync(
        Guid provisionedUserId, string externalUserId, SigningConsentWitness authority, CancellationToken ct = default);
}

/// <inheritdoc/>
public sealed class TenantProvisionedGrantIssuer(SurrealConsentLifecycleStore store) : ITenantProvisionedGrantIssuer
{
    /// <inheritdoc/>
    public async Task<AZOAResult<ConsentLifecycleReceipt>> IssueOnProvisionAsync(
        Guid provisionedUserId, string externalUserId, SigningConsentWitness authority, CancellationToken ct = default)
    {
        var receipt = await store.AdmitProvisionedAsync(provisionedUserId, externalUserId, authority, ct);
        return receipt is null
            ? AZOAResult<ConsentLifecycleReceipt>.Failure("Current tenant ownership, provisioning authority and open membership are required.")
            : AZOAResult<ConsentLifecycleReceipt>.Success(receipt);
    }
}

/// <summary>Registers the atomic tenant provisioning consent issuer.</summary>
public static class TenantProvisionedGrantRegistration
{
    public static IServiceCollection AddTenantProvisionedConsentGrants(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.All(descriptor => descriptor.ServiceType != typeof(SurrealConsentLifecycleStore)))
            throw new InvalidOperationException("Tenant consent provisioning requires the atomic lifecycle store.");
        services.AddScoped<ITenantProvisionedGrantIssuer, TenantProvisionedGrantIssuer>();
        return services;
    }
}
