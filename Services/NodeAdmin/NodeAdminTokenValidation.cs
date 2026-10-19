using System.Globalization;
using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>The <c>OnTokenValidated</c> node-admin branch (H6): runs first and always ends in fail-or-return; see <c>Services/NodeAdmin/AGENTS.md</c> §token-validation.</summary>
public static class NodeAdminTokenValidation
{
    /// <summary>Validates a node-admin token against its live session, admin row or root state; fails it on any other endpoint.</summary>
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        if (!EndpointAcceptsNodeAdmin(context.HttpContext))
        {
            context.Fail("Node-admin sessions are not accepted by this endpoint.");
            return;
        }
        var validator = context.HttpContext.RequestServices.GetRequiredService<NodeAdminSessionValidator>();
        var caller = await validator.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);
        if (caller is null)
        {
            context.Fail("Node-admin session is not valid.");
            return;
        }
        context.HttpContext.Items[NodeAdminCaller.ItemKey] = caller;
    }

    /// <summary>True only on a <c>NodeAdmin:*</c> endpoint or an operator route mapped to a permission (fail closed everywhere else).</summary>
    public static bool EndpointAcceptsNodeAdmin(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy?.StartsWith(NodeAdminPolicies.Prefix, StringComparison.Ordinal) == true) == true)
            return true;
        return NodeAdminRoutePermissions.TryResolve(endpoint, context.Request.Method, out var grant) && grant.Permission is not null;
    }
}

/// <summary>Checks a node-admin principal against durable state on every request (C2).</summary>
public sealed class NodeAdminSessionValidator(
    SurrealNodeAdminStore store,
    NodeAdminRuntime runtime,
    NodeAdminPermissionResolver resolver,
    TimeProvider clock)
{
    public async Task<NodeAdminCaller?> ValidateAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        var subject = NodeAdminClaims.Find(principal, NodeAdminClaims.Subject);
        var sid = NodeAdminClaims.Find(principal, NodeAdminClaims.SessionId);
        if (subject is null || !NodeAdminCrypto.TrySessionRecordId(sid, out var sessionRecordId)) return null;
        var now = clock.GetUtcNow();
        var session = await store.GetSessionAsync(sessionRecordId, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now
            || !string.Equals(session.Principal, subject, StringComparison.Ordinal))
            return null;

        if (subject.StartsWith(NodeAdminClaims.AdminPrincipalPrefix, StringComparison.Ordinal))
        {
            if (!Guid.TryParseExact(subject[NodeAdminClaims.AdminPrincipalPrefix.Length..], "D", out var adminId)
                || !TryReadLong(principal, NodeAdminClaims.AdminRevision, out var adminRevision)
                || !TryReadLong(principal, NodeAdminClaims.SessionRevision, out var sessionRevision))
                return null;
            var admin = await store.GetAdminAsync(adminId.ToString("N"), ct);
            if (admin is null || admin.Status != NodeAdminAccount.StatusActive
                || admin.AdminRevision != adminRevision || admin.SessionRevision != sessionRevision)
                return null;
            var roles = await resolver.SnapshotAsync(store, ct);
            return new NodeAdminCaller(subject, admin, null, sessionRecordId, session.AuthTime, session.Amr, roles.EffectivePermissions(admin));
        }

        if (subject.StartsWith(NodeAdminClaims.RootPrincipalPrefix, StringComparison.Ordinal))
        {
            var fingerprint = subject[NodeAdminClaims.RootPrincipalPrefix.Length..];
            if (!NodeAdminCrypto.IsFingerprint(fingerprint) || !runtime.RootLoginEnabled || !runtime.IsRootFingerprint(fingerprint)
                || !TryReadLong(principal, NodeAdminClaims.RootKeyRevision, out var keyRevision)
                || !TryReadLong(principal, NodeAdminClaims.RootSessionRevision, out var rootSessionRevision))
                return null;
            var state = await store.GetRootStateAsync(ct);
            if (state is null || state.RootKeyRevision != keyRevision || runtime.RootKeyRevision != keyRevision
                || state.RootSessionRevision != rootSessionRevision)
                return null;
            return new NodeAdminCaller(subject, null, fingerprint, sessionRecordId, session.AuthTime, session.Amr, NodeAdminPermissions.Root);
        }

        return null;
    }

    private static bool TryReadLong(ClaimsPrincipal? principal, string claim, out long value)
        => long.TryParse(NodeAdminClaims.Find(principal, claim), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
