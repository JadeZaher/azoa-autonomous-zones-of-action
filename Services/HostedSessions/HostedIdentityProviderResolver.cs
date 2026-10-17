// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>
/// Finds the hosted provider that may re-fetch a persisted KYC/KYB session, from the CURRENT
/// authority (node default or the tenant's selection). A session whose profile is no longer the
/// selected one resolves to null and is left to expire.
/// </summary>
public sealed class HostedIdentityProviderResolver(IKycProviderRegistry registry, IdentomatKybProvider kyb)
{
    /// <summary>The bound provider for <paramref name="session"/>, or null when no current authority owns it.</summary>
    public async Task<IHostedSessionProvider?> ResolveAsync(HostedSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.FlowKind == HostedSessionFlowKinds.Kyb)
            return string.Equals(session.ProviderKey, kyb.ProviderKey, StringComparison.Ordinal) ? kyb : null;
        if (session.FlowKind != HostedSessionFlowKinds.Kyc)
            return null;

        var resolution = session.TenantId is null
            ? registry.ResolveNodeDefault()
            : Guid.TryParseExact(session.TenantId, "N", out var tenantId)
                ? await registry.ResolveTenantAsync(tenantId, ct)
                : null;
        return resolution is { IsError: false, Result.Provider: IHostedSessionProvider hosted }
            && string.Equals(hosted.ProviderKey, session.ProviderKey, StringComparison.Ordinal)
            ? hosted
            : null;
    }
}
