using System.Collections.Concurrent;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>The validated node-admin caller of one request, attached by the token-validation branch.</summary>
public sealed record NodeAdminCaller(
    string Principal,
    NodeAdminAccount? Admin,
    string? RootFingerprint,
    string SessionRecordId,
    DateTimeOffset AuthTime,
    IReadOnlyList<string> Amr,
    IReadOnlySet<string> Permissions)
{
    public const string ItemKey = "AZOA.NodeAdminCaller";

    public bool IsRoot => RootFingerprint is not null;

    /// <summary>Step-up window: authenticated within the last 10 minutes (same as RecentNodeOperatorSession).</summary>
    public bool IsRecent(DateTimeOffset now)
    {
        var age = now - AuthTime;
        return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(10);
    }

    public static NodeAdminCaller? From(HttpContext? context)
        => context?.Items.TryGetValue(ItemKey, out var value) == true ? value as NodeAdminCaller : null;
}

/// <summary>Policy names <c>NodeAdmin:&lt;permission&gt;[:recent]</c>; <c>session</c> means any live node-admin session.</summary>
public static class NodeAdminPolicies
{
    public const string Prefix = "NodeAdmin:";
    public const string RecentSuffix = ":recent";
    public const string Session = "NodeAdmin:session";
    public const string SessionRecent = "NodeAdmin:session:recent";
    public const string AdminsRead = "NodeAdmin:admins.read";
    public const string AdminsManageRecent = "NodeAdmin:admins.manage:recent";
    public const string RolesRead = "NodeAdmin:roles.read";
    public const string RolesManageRecent = "NodeAdmin:roles.manage:recent";
    public const string AuditRead = "NodeAdmin:audit.read";

    /// <summary>Parses a node-admin policy name; false for any other or unknown permission (fail closed).</summary>
    public static bool TryParse(string? policyName, out string? permission, out bool requireRecent)
    {
        permission = null;
        requireRecent = false;
        if (policyName is null || !policyName.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = policyName[Prefix.Length..];
        if (rest.EndsWith(RecentSuffix, StringComparison.Ordinal))
        {
            requireRecent = true;
            rest = rest[..^RecentSuffix.Length];
        }
        if (rest == "session") return true;
        if (!NodeAdminPermissions.IsKnown(rest)) return false;
        permission = rest;
        return true;
    }
}

/// <summary>Requires a validated node-admin caller holding <see cref="Permission"/> (null = any session), optionally with recent authentication.</summary>
public sealed record NodeAdminPermissionRequirement(string? Permission, bool RequireRecent) : IAuthorizationRequirement;

/// <summary>Evaluates <see cref="NodeAdminPermissionRequirement"/> against the live permissions computed at token validation.</summary>
public sealed class NodeAdminPermissionHandler(TimeProvider clock) : AuthorizationHandler<NodeAdminPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NodeAdminPermissionRequirement requirement)
    {
        var caller = NodeAdminCaller.From(context.Resource as HttpContext);
        if (caller is null || !NodeAdminClaims.IsNodeAdmin(context.User)
            || (requirement.Permission is not null && !caller.Permissions.Contains(requirement.Permission)))
        {
            context.Fail(new AuthorizationFailureReason(this, NodeAdminErrorCodes.PermissionDenied));
        }
        else if (requirement.RequireRecent && !caller.IsRecent(clock.GetUtcNow()))
        {
            context.Fail(new AuthorizationFailureReason(this, NodeAdminErrorCodes.StepUpRequired));
        }
        else
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Resolves <c>NodeAdmin:*</c> policy names on demand; every other name falls through to the registered policies.</summary>
public sealed class NodeAdminAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _policies = new(StringComparer.Ordinal);

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(NodeAdminPolicies.Prefix, StringComparison.Ordinal))
            return base.GetPolicyAsync(policyName);
        if (!NodeAdminPolicies.TryParse(policyName, out var permission, out var recent))
            return Task.FromResult<AuthorizationPolicy?>(null);
        return Task.FromResult<AuthorizationPolicy?>(_policies.GetOrAdd(policyName, _ => new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new NodeAdminPermissionRequirement(permission, recent))
            .Build()));
    }
}
