using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
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
    public async Task Node_admin_writes_require_step_up_even_where_the_legacy_operator_does_not()
    {
        var root = await RootLoginAsync();
        var operations = await CreateActiveAdminAsync(root, root, "ops-stale", "operations");
        await AgeSessionAsync(operations.Token, TimeSpan.FromMinutes(11));
        var refund = $"/api/admin/on-ramp-lots/intents/{Guid.NewGuid()}/refund";

        // D-M1: on-ramp refund carries only the legacy Operator policy, yet a node admin needs a recent session for it.
        var stale = await SendToAsync(HttpMethod.Post, refund, new { reason = "customer asked for a refund" }, operations.Token);
        stale.Status.Should().Be(HttpStatusCode.Forbidden);
        stale.Code.Should().Be(NodeAdminErrorCodes.StepUpRequired);
        stale.AuthRequirement.Should().Be(NodeAdminErrorCodes.RecentNodeAdminLogin);

        // A valid stale login must be newer than the operator's credential watermark.
        var legacy = await LegacyTokenAsync(authAge: TimeSpan.FromMinutes(30));
        (await SendToAsync(HttpMethod.Post, refund, new { reason = "before credential cut" }, legacy))
            .Status.Should().Be(HttpStatusCode.Unauthorized, "a token before the seeded credential watermark must remain invalid");
        using var scope = Factory.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
        var operatorId = NodeOperatorIdentity.UserId.ToString("N");
        var original = await executor.QuerySingleAsync<User>(SurrealQuery<User>.Key(operatorId), CancellationToken.None);
        original.Should().NotBeNull();
        var watermark = original!.AuthNotBefore;
        (await executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operatorId).Where(row => true)
            .Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow.AddHours(-1)).Build(), CancellationToken.None)).EnsureAllOk();
        try
        {
            var legacyRefund = await SendToAsync(HttpMethod.Post, refund, new { reason = "customer asked for a refund" }, legacy);
            legacyRefund.Status.Should().NotBe(HttpStatusCode.Forbidden, legacyRefund.Body.ToString())
                .And.NotBe(HttpStatusCode.Unauthorized, "the legacy Operator policy has no step-up; the domain answers the unknown intent");
            (await SendToAsync(HttpMethod.Post, $"/api/operator/kyc/submissions/{Guid.NewGuid()}/decision", new { decision = "approve" }, legacy))
                .Status.Should().Be(HttpStatusCode.Forbidden, "the same valid stale login still fails a legacy recent-login policy");
        }
        finally
        {
            (await executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(operatorId).Where(row => true)
                .Set(row => row.AuthNotBefore, watermark).Build(), CancellationToken.None)).EnsureAllOk();
        }
    }

    [Fact]
    public async Task Session_revocations_require_step_up_and_denial_leaves_the_original_session_live()
    {
        var root = await RootLoginAsync();
        var me = await SendAsync(HttpMethod.Get, "/me", token: root);
        var handle = me.Result.GetProperty("session").GetProperty("handle").GetString()!;
        await AgeSessionAsync(root, TimeSpan.FromMinutes(11));

        foreach (var path in new[] { "/auth/revoke", $"/sessions/{handle}/revoke" })
        {
            var stale = await SendAsync(HttpMethod.Post, path, token: root);
            stale.Status.Should().Be(HttpStatusCode.Forbidden);
            stale.Code.Should().Be(NodeAdminErrorCodes.StepUpRequired);
            stale.AuthRequirement.Should().Be(NodeAdminErrorCodes.RecentNodeAdminLogin);
        }
        (await SendAsync(HttpMethod.Get, "/me", token: root)).Status.Should().Be(HttpStatusCode.OK);

        var recent = await RootLoginAsync();
        (await SendAsync(HttpMethod.Post, $"/sessions/{handle}/revoke", token: recent)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/me", token: root)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/me", token: recent)).Status.Should().Be(HttpStatusCode.OK,
            "revoking by the original handle preserves the replacement session");
    }

    [Fact]
    public async Task Node_admin_reads_of_kyc_data_are_chained_as_read_rows_naming_the_admin_without_query_values()
    {
        var root = await RootLoginAsync();
        var compliance = await CreateActiveAdminAsync(root, root, "kyc-reader", "compliance");
        const string probe = "pii-probe@example.test";

        (await SendToAsync(HttpMethod.Get, $"/api/operator/kyc/submissions?status=pending&limit=10&probe={Uri.EscapeDataString(probe)}",
            token: compliance.Token)).Status.Should().Be(HttpStatusCode.OK);
        (await SendToAsync(HttpMethod.Get, FeeAudit, token: root)).Status.Should().Be(HttpStatusCode.OK);
        (await SendToAsync(HttpMethod.Get, "/api/user", token: compliance.Token)).Status.Should().Be(HttpStatusCode.OK);
        await AgeSessionAsync(compliance.Token, TimeSpan.FromMinutes(11));
        (await SendToAsync(HttpMethod.Post, "/api/search/operator", new { query = probe, entityTypes = 1 }, compliance.Token))
            .Status.Should().Be(HttpStatusCode.OK, "the allow-listed body-carried query remains a read even with an old session");

        var rows = await AuditRowsAsync(root);
        var read = rows.Should().ContainSingle(row => row.GetProperty("actor").GetString() == "admin:" + compliance.Id
            && row.GetProperty("action").GetString() == "GET /api/operator/kyc/submissions").Subject;
        read.GetProperty("outcome").GetString().Should().Be(NodeAdminAuditLog.OutcomeRead, "a read gets one read row, never an intent row");
        var detail = read.GetProperty("detail").GetString()!;
        detail.Should().Be("""{"status":200,"permission":"kyc.read","query":["limit","probe","status"]}""");
        detail.Should().NotContain("pii-probe").And.NotContain("pending", "query values never reach the chain");
        var searchRead = rows.Should().ContainSingle(row => row.GetProperty("actor").GetString() == "admin:" + compliance.Id
            && row.GetProperty("action").GetString() == "POST /api/Search/operator").Subject;
        searchRead.GetProperty("outcome").GetString().Should().Be(NodeAdminAuditLog.OutcomeRead);
        searchRead.GetProperty("detail").GetString().Should().NotContain(probe, "body values never reach read-audit metadata");
        rows.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + compliance.Id
            && row.GetProperty("action").GetString()!.ToLowerInvariant() == "get /api/user" && row.GetProperty("outcome").GetString() == NodeAdminAuditLog.OutcomeRead);

        rows.Should().NotContain(row => row.GetProperty("action").GetString() == "GET /api/node-governance/fee-audit",
            "audit.read reads are not chained (they would only audit the audit)");
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

    /// <summary>A real legacy operator token re-signed with an older <c>auth_time</c> (same claims, revisions and signing key).</summary>
    private async Task<string> LegacyTokenAsync(TimeSpan authAge)
    {
        var login = await SendToAsync(HttpMethod.Post, "/api/operator/session", new
        {
            username = NodeAdminOperatorRoutesFactory.OperatorUsername,
            password = NodeAdminOperatorRoutesFactory.OperatorPassword,
        });
        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        var payload = new JwtSecurityTokenHandler().ReadJwtToken(login.Result.GetProperty("accessToken").GetString()!).Payload;
        payload[AzoaClaims.AuthTime] = (DateTimeOffset.UtcNow - authAge).ToUnixTimeSeconds();
        var signing = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(NodeAdminWebApplicationFactory.JwtKey)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(signing), payload));
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
