using System.Net;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Real-auth host with the legacy reserved operator seeded beside named node admins, plus the governance and KYC tables.</summary>
public sealed class NodeAdminOperatorRoutesFactory : NodeAdminWebApplicationFactory
{
    public const string OperatorUsername = "node-operator";
    public static readonly string OperatorPassword = "Op-" + Convert.ToHexString(Guid.NewGuid().ToByteArray()) + "x9";

    protected override IEnumerable<string> ExtraTables =>
    [
        "node_fee_schedule", "node_fee_audit", "node_governance_parameters", "node_governance_audit",
        "kyc_provider_profile", "kyc_control_audit", "kyc_submission", "tenant_kyc_provider_selection",
    ];

    protected override IEnumerable<KeyValuePair<string, string>> ExtraSettings =>
    [
        new("NodeOperator:Username", OperatorUsername),
        new("NodeOperator:Password", OperatorPassword),
        new("NodeOperator:CredentialRevision", "1"),
    ];
}

/// <summary>
/// Operator routes under dual-accepting policies (plan §4, §8 C3/M3/M7): named admins act only through the mapped permission,
/// step-up mirrors the legacy recent-login rule, root never governs, the legacy operator token is unchanged, and the domain
/// audits name the real actor. Requires the local SurrealDB at <see cref="SurrealTestDefaults.Endpoint"/>.
/// </summary>
public sealed class NodeAdminOperatorRouteFlowTests(NodeAdminOperatorRoutesFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminOperatorRoutesFactory>
{
    private const string FeeSchedule = "/api/node-governance/fee-schedule";
    private const string FeeAudit = "/api/node-governance/fee-audit";
    private static readonly object FeeUpdate = new { mint = new { flatBaseUnits = "7" } };

    [Fact]
    public async Task Named_admins_reach_operator_routes_only_through_their_role_permissions_and_audits_name_them()
    {
        var root = await RootLoginAsync();
        var governance = await CreateActiveAdminAsync(root, root, "gov-admin", "governance");
        var compliance = await CreateActiveAdminAsync(root, root, "kyc-admin", "compliance");
        var auditor = await CreateActiveAdminAsync(root, root, "audit-admin", "auditor");

        // Governance admin: fee-schedule write succeeds and its fee-audit row names admin:<id>; payout mutations are out of reach.
        (await SendToAsync(HttpMethod.Put, FeeSchedule, FeeUpdate, governance.Token)).Status.Should().Be(HttpStatusCode.OK);
        var feeAudit = await SendToAsync(HttpMethod.Get, FeeAudit, token: governance.Token);
        feeAudit.Status.Should().Be(HttpStatusCode.OK);
        feeAudit.Result.EnumerateArray().First().GetProperty("actor").GetString().Should().Be("admin:" + governance.Id);
        var payout = await SendToAsync(HttpMethod.Post, $"/api/admin/fiat-payouts/{Guid.NewGuid():N}/park", new { reason = "governance has money.read only" }, governance.Token);
        payout.Status.Should().Be(HttpStatusCode.Forbidden, "payout mutations need money.operate (governance holds every .read, not money writes)");
        payout.Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);

        // Compliance admin: a KYC decision is admitted (the domain answers, not the policy) but the fee schedule is not.
        var decision = await SendToAsync(HttpMethod.Post, $"/api/operator/kyc/submissions/{Guid.NewGuid()}/decision",
            new { decision = "approve" }, compliance.Token);
        decision.Status.Should().Be(HttpStatusCode.NotFound, "kyc.decide admits the call; the unknown submission is the domain's answer");
        var denied = await SendToAsync(HttpMethod.Put, FeeSchedule, FeeUpdate, compliance.Token);
        denied.Status.Should().Be(HttpStatusCode.Forbidden);
        denied.Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);

        // Compliance configures a KYC provider; the KYC control audit names the admin.
        (await SendToAsync(HttpMethod.Put, "/api/operator/kyc/providers/manual", new
        {
            displayName = "Manual review", adapterKey = "manual", enabled = false, policyVersion = "manual-v1", assuranceLevel = "manual",
        }, compliance.Token)).Status.Should().Be(HttpStatusCode.OK);

        // Auditor: governance and KYC reads (domain audits included), no writes.
        (await SendToAsync(HttpMethod.Get, FeeSchedule, token: auditor.Token)).Status.Should().Be(HttpStatusCode.OK);
        var kycAudit = await SendToAsync(HttpMethod.Get, "/api/operator/kyc/audit?limit=10", token: auditor.Token);
        kycAudit.Status.Should().Be(HttpStatusCode.OK);
        kycAudit.Result.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("actor").GetString())
            .Should().Contain("admin:" + compliance.Id);
        (await SendToAsync(HttpMethod.Get, "/api/operator/kyc/submissions?status=pending&limit=10", token: auditor.Token)).Status.Should().Be(HttpStatusCode.OK);
        (await SendToAsync(HttpMethod.Put, FeeSchedule, FeeUpdate, auditor.Token)).Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);
        (await SendToAsync(HttpMethod.Post, $"/api/operator/kyc/submissions/{Guid.NewGuid()}/decision", new { decision = "approve" }, auditor.Token))
            .Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);

        // Every node-admin write on an operator route is also chained in the node-admin audit, with its denial rows.
        var chained = await AuditRowsAsync(root);
        chained.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + governance.Id
            && row.GetProperty("action").GetString() == "PUT /api/node-governance/fee-schedule" && row.GetProperty("outcome").GetString() == "ok");
        chained.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + compliance.Id
            && row.GetProperty("action").GetString() == "authz.denied");
    }

    [Fact]
    public async Task Step_up_routes_require_a_recent_session_and_root_never_governs()
    {
        var root = await RootLoginAsync();
        var compliance = await CreateActiveAdminAsync(root, root, "stale-kyc", "compliance");
        await AgeSessionAsync(compliance.Token, TimeSpan.FromMinutes(11));

        var stale = await SendToAsync(HttpMethod.Post, $"/api/operator/kyc/submissions/{Guid.NewGuid()}/decision",
            new { decision = "approve" }, compliance.Token);
        stale.Status.Should().Be(HttpStatusCode.Forbidden);
        stale.Code.Should().Be(NodeAdminErrorCodes.StepUpRequired);
        stale.AuthRequirement.Should().Be(NodeAdminErrorCodes.RecentNodeAdminLogin);
        (await SendToAsync(HttpMethod.Get, "/api/operator/kyc/submissions?status=pending&limit=10", token: compliance.Token))
            .Status.Should().Be(HttpStatusCode.OK, "a non-step-up read is unaffected by session age");

        // M3: break-glass root holds people management and audit.read only.
        var rootWrite = await SendToAsync(HttpMethod.Put, FeeSchedule, FeeUpdate, root);
        rootWrite.Status.Should().Be(HttpStatusCode.Forbidden);
        rootWrite.Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);
        (await SendToAsync(HttpMethod.Get, FeeAudit, token: root)).Status.Should().Be(HttpStatusCode.OK, "audit.read covers domain audits");

        // A legacy-only operator route never admits a node-admin token, whatever its roles (refused at token validation).
        var owner = await CreateActiveAdminAsync(root, root, "owner-x", "node-owner");
        (await SendToAsync(HttpMethod.Post, "/api/operator/session/revoke", token: owner.Token)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendToAsync(HttpMethod.Get, $"/api/user/{Guid.NewGuid()}", token: owner.Token)).Status
            .Should().Be(HttpStatusCode.Unauthorized, "a node-admin token is refused on a plain user route");
    }

    [Fact]
    public async Task Legacy_operator_token_keeps_working_on_every_operator_policy_and_audits_as_legacy_operator()
    {
        var login = await SendToAsync(HttpMethod.Post, "/api/operator/session", new
        {
            username = NodeAdminOperatorRoutesFactory.OperatorUsername,
            password = NodeAdminOperatorRoutesFactory.OperatorPassword,
        });
        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        var legacy = login.Result.GetProperty("accessToken").GetString()!;

        (await SendToAsync(HttpMethod.Put, FeeSchedule, new { mint = new { flatBaseUnits = "11" } }, legacy)).Status.Should().Be(HttpStatusCode.OK); // NodeGovern
        var feeAudit = await SendToAsync(HttpMethod.Get, FeeAudit, token: legacy);
        feeAudit.Result.EnumerateArray().First().GetProperty("actor").GetString().Should().Be(OperatorActor.LegacyAuditActor);
        (await SendToAsync(HttpMethod.Get, "/api/user", token: legacy)).Status.Should().Be(HttpStatusCode.OK); // Operator
        (await SendToAsync(HttpMethod.Get, "/api/operator/kyc/submissions?status=pending&limit=10", token: legacy)).Status.Should().Be(HttpStatusCode.OK); // NodeOperatorSession
        (await SendToAsync(HttpMethod.Post, $"/api/operator/kyc/submissions/{Guid.NewGuid()}/decision", new { decision = "approve" }, legacy))
            .Status.Should().Be(HttpStatusCode.NotFound); // RecentNodeOperatorSession admits; the domain answers
        (await SendToAsync(HttpMethod.Get, Base + "/me", token: legacy)).Status
            .Should().Be(HttpStatusCode.Unauthorized, "a legacy operator token never satisfies a NodeAdmin:* policy");
    }

    [Fact]
    public async Task Dapp_role_assignment_by_a_node_admin_and_by_a_user_manager_both_work()
    {
        var root = await RootLoginAsync();
        var owner = await CreateActiveAdminAsync(root, root, "owner-roles", "node-owner");
        var manager = await RegisterUserAsync("mgr");
        var developer = await RegisterUserAsync("dev");

        // Node admin with users.roles.assign takes the operator branch of DappRoleAssignment (may mint a manager).
        var promoted = await SendToAsync(HttpMethod.Put, $"/api/user/{manager.Id}/dapp-role", new { role = AzoaDappRoles.Manager }, owner.Token);
        promoted.Status.Should().Be(HttpStatusCode.OK, promoted.Body.ToString());

        // CanAssignDappRoles user path is unchanged: the manager (fresh login) assigns developer, but never manager.
        var managerToken = await LoginUserAsync(manager.Email, manager.Password);
        (await SendToAsync(HttpMethod.Put, $"/api/user/{developer.Id}/dapp-role", new { role = AzoaDappRoles.Developer }, managerToken))
            .Status.Should().Be(HttpStatusCode.OK);
        (await SendToAsync(HttpMethod.Put, $"/api/user/{developer.Id}/dapp-role", new { role = AzoaDappRoles.Manager }, managerToken))
            .Status.Should().Be(HttpStatusCode.Forbidden);

        // A node admin without users.roles.assign is denied by the same policy.
        var auditor = await CreateActiveAdminAsync(root, root, "audit-roles", "auditor");
        (await SendToAsync(HttpMethod.Put, $"/api/user/{developer.Id}/dapp-role", new { role = AzoaDappRoles.User }, auditor.Token))
            .Code.Should().Be(NodeAdminErrorCodes.PermissionDenied);
    }

    private async Task AgeSessionAsync(string token, TimeSpan age)
    {
        NodeAdminCrypto.TrySessionRecordId(SidOf(token), out var recordId).Should().BeTrue();
        using var scope = Factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<NodeAdminSession>(recordId)
            .Where(row => row.RevokedAt == null).Set(row => row.AuthTime, DateTimeOffset.UtcNow - age).Build(), CancellationToken.None);
        response.EnsureAllOk();
    }

    private async Task<(Guid Id, string Email, string Password)> RegisterUserAsync(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"{prefix}-{suffix}@example.test";
        const string password = "Correct-Horse-Battery-9!";
        var registered = await SendToAsync(HttpMethod.Post, "/api/user/register", new { username = $"{prefix}{suffix}", email, password });
        registered.Status.Should().Be(HttpStatusCode.OK, registered.Body.ToString());
        return (Guid.Parse(registered.Result.GetProperty("id").GetString()!), email, password);
    }

    private async Task<string> LoginUserAsync(string email, string password)
    {
        var login = await SendToAsync(HttpMethod.Post, "/api/user/login", new { email, password });
        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        return login.Result.GetString()!;
    }
}
