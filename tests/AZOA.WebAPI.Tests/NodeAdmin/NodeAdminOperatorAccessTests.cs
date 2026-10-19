using System.Security.Claims;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>The node-admin branch of the dual-accepting operator policies against real route endpoints: mapped permission,
/// step-up, M3 root ceiling and M7 fail-closed on an unmapped operator route.</summary>
public sealed class NodeAdminOperatorAccessTests
{
    private static readonly string[] Governance = [NodeAdminPermissions.GovernanceRead, NodeAdminPermissions.GovernanceWrite, NodeAdminPermissions.AuditRead];

    public static TheoryData<string, string, string, string[], bool, int, string?> Cases => new()
    {
        // label, verb, template, caller permissions, root?, session age (minutes), expected code (null = admitted)
        { "governance admin writes the fee schedule", "PUT", "api/node-governance/fee-schedule", Governance, false, 2, null },
        { "a node-admin write needs step-up even where legacy does not (D-M1)", "PUT", "api/node-governance/fee-schedule", Governance, false, 11, NodeAdminErrorCodes.StepUpRequired },
        { "a non-step-up read ignores session age", "GET", "api/node-governance/fee-schedule", Governance, false, 30, null },
        { "compliance admin cannot write the fee schedule", "PUT", "api/node-governance/fee-schedule", [NodeAdminPermissions.KycDecide], false, 1, NodeAdminErrorCodes.PermissionDenied },
        { "step-up route with a stale session", "POST", "api/operator/kyc/submissions/{id:guid}/decision", [NodeAdminPermissions.KycDecide], false, 11, NodeAdminErrorCodes.StepUpRequired },
        { "step-up route with a recent session", "POST", "api/operator/kyc/submissions/{id:guid}/decision", [NodeAdminPermissions.KycDecide], false, 2, null },
        { "root never governs (M3)", "PUT", "api/node-governance/fee-schedule", NodeAdminPermissions.Root.ToArray(), true, 1, NodeAdminErrorCodes.PermissionDenied },
        { "root reads domain audits with audit.read", "GET", "api/node-governance/fee-audit", NodeAdminPermissions.Root.ToArray(), true, 1, null },
        { "legacy-only route denies even an owner", "POST", "api/operator/session/revoke", NodeAdminPermissions.All.ToArray(), false, 1, NodeAdminErrorCodes.PermissionDenied },
        { "unmapped operator route denies even an owner (M7)", "POST", "api/operator/unmapped-test-route", NodeAdminPermissions.All.ToArray(), false, 1, NodeAdminErrorCodes.PermissionDenied },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Operator_policies_admit_a_node_admin_only_through_the_mapped_grant(
        string label, string verb, string template, string[] permissions, bool root, int authAgeMinutes, string? expected)
    {
        var http = Request(verb, template, "RecentNodeOperatorSession", Caller(permissions, root, authAgeMinutes));
        var context = new AuthorizationHandlerContext([new AssertionRequirement(_ => true)], http.User, http);

        var admitted = NodeAdminOperatorAccess.Admit(context);

        admitted.Should().Be(expected is null, label);
        context.FailureReasons.Select(reason => reason.Message).Should().Equal(expected is null ? [] : [expected], label);
        NodeAdminTokenValidation.EndpointAcceptsNodeAdmin(http).Should().Be(
            NodeAdminRoutePermissions.Map.TryGetValue(NodeAdminRoutePermissions.Key(verb, template), out var grant) && grant.Permission is not null,
            "token validation admits node admins exactly on mapped operator routes");
    }

    [Fact]
    public void Non_node_admin_principals_fall_through_to_the_unchanged_legacy_predicate()
    {
        var http = Request("PUT", "api/node-governance/fee-schedule", "NodeGovern", caller: null);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator)], "Bearer"));
        var context = new AuthorizationHandlerContext([new AssertionRequirement(_ => true)], http.User, http);

        NodeAdminOperatorAccess.Admit(context).Should().BeNull();
        context.FailureReasons.Should().BeEmpty();
        NodeAdminTokenValidation.EndpointAcceptsNodeAdmin(Request("GET", "api/user/{id:guid}", "FirstPartyLogin", caller: null))
            .Should().BeFalse("a node-admin token is refused on a route without an operator policy");
    }

    private static NodeAdminCaller Caller(string[] permissions, bool root, int authAgeMinutes)
        => new(root ? "root:21fe31dfa154a261" : NodeAdminClaims.AdminPrincipal(Guid.NewGuid()), null, root ? "21fe31dfa154a261" : null,
            new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(-authAgeMinutes), ["ed25519"], permissions.ToHashSet(StringComparer.Ordinal));

    private static DefaultHttpContext Request(string verb, string template, string policy, NodeAdminCaller? caller)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = verb;
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0,
            new EndpointMetadataCollection(new AuthorizeAttribute(policy)), template));
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AzoaClaims.TokenUse, NodeAdminClaims.TokenUseNodeAdmin)], "Bearer"));
        if (caller is not null) http.Items[NodeAdminCaller.ItemKey] = caller;
        return http;
    }
}
