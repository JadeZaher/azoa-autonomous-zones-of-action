using System.Net;
using System.Net.Http.Headers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>
/// Node-admin flows through the real HTTP pipeline (production JWT validation, real SurrealDB, real Ed25519 and TOTP):
/// root bootstrap → create admin → enroll → activate → TOTP login → permitted/forbidden calls → revoke → audit chain.
/// Requires the local SurrealDB at <see cref="SurrealTestDefaults.Endpoint"/>.
/// </summary>
public sealed class NodeAdminFlowTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    [Fact]
    public async Task Root_bootstraps_an_owner_who_enrolls_logs_in_with_totp_and_is_bound_by_rbac_invariants()
    {
        var root = await RootLoginAsync();
        var me = await SendAsync(HttpMethod.Get, "/me", token: root);
        me.Status.Should().Be(HttpStatusCode.OK);
        me.Result.GetProperty("principal").GetString().Should().Be("root:" + Factory.RootKey.Fingerprint);
        Strings(me.Result.GetProperty("permissions")).Should().NotContain(["money.operate", "kyc.decide", "governance.write"]);

        // Root mints the first owner; the enrollment token is returned once and binds exactly one key.
        var (aliceId, aliceToken0) = await CreateAdminAsync(root, "alice", "node-owner");
        var started = await StartEnrollmentAsync(aliceToken0);
        (await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = aliceToken0, publicKey = NodeAdminTestKey.Generate().PublicKey, label = "x" }))
            .Code.Should().Be("ENROLLMENT_ALREADY_STARTED", "an enrollment binds exactly one key (H3)");
        var alice = await CompleteEnrollmentAsync(started.Key, started.Start);
        started.Start.GetProperty("otpauthUri").GetString().Should().Contain("issuer=AZOA%20Node%20(node.example.test)");

        // Activation confirms the enrolled key's fingerprint out of band.
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/activate", new { fingerprint = "0000000000000000" }, root))
            .Code.Should().Be("FINGERPRINT_MISMATCH");
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/activate", new { fingerprint = alice.Key.Fingerprint }, root))
            .Result.GetProperty("status").GetString().Should().Be("active");

        // TOTP login; the same code (same step) cannot be replayed.
        var code = alice.CurrentCode();
        var login = await AdminLoginAsync(alice.Key, code);
        login.Status.Should().Be(HttpStatusCode.OK);
        var aliceToken = login.Result.GetProperty("accessToken").GetString()!;
        Strings(login.Result.GetProperty("permissions")).Should().Contain("money.operate");
        (await AdminLoginAsync(alice.Key, code)).Code.Should().Be("AUTH_FAILED", "a TOTP step is single use");

        // Permitted calls for the owner; a weaker admin is refused dominance, escalation and permissions.
        (await SendAsync(HttpMethod.Get, "/admins", token: aliceToken)).Status.Should().Be(HttpStatusCode.OK);
        var people = await SendAsync(HttpMethod.Post, "/roles", new { name = "people", permissions = new[] { "admins.read", "admins.manage", "roles.read" } }, aliceToken);
        var peopleRoleId = people.Result.GetProperty("id").GetString()!;
        var bob = await CreateActiveAdminAsync(aliceToken, aliceToken, "bob", peopleRoleId);
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: bob.Token)).Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Post, "/admins", new { username = "mallory", displayName = "M", roleIds = new[] { "node-owner" } }, bob.Token))
            .Code.Should().Be("ESCALATION_REFUSED");
        var denied = await SendAsync(HttpMethod.Get, "/audit", token: bob.Token);
        denied.Status.Should().Be(HttpStatusCode.Forbidden);
        denied.Code.Should().Be("PERMISSION_DENIED");

        // Last owner: even root cannot suspend or demote the only owner; nobody suspends themselves.
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: root)).Code.Should().Be("LAST_OWNER");
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: aliceToken)).Code.Should().Be("SELF_ACTION_REFUSED");
        (await SendAsync(HttpMethod.Put, $"/admins/{aliceId}/roles", new { roleIds = new[] { peopleRoleId } }, root)).Code.Should().Be("LAST_OWNER");

        // Revoking the session kills the token immediately (server-side session, C2).
        (await SendAsync(HttpMethod.Post, "/auth/revoke", token: aliceToken)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/me", token: aliceToken)).Status.Should().Be(HttpStatusCode.Unauthorized);

        // The audit names the human actors, records the denial and verifies; tampering one row breaks the chain there.
        var rows = await AuditRowsAsync(root);
        rows.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + bob.Id
            && row.GetProperty("action").GetString() == "authz.denied");
        rows.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + aliceId
            && row.GetProperty("action").GetString() == "POST /api/node-admin/admins" && row.GetProperty("outcome").GetString() == "ok");
        rows.Should().Contain(row => row.GetProperty("action").GetString() == "auth.session" && row.GetProperty("outcome").GetString() == "failed");
        var verified = await SendAsync(HttpMethod.Get, "/audit/verify", token: root);
        verified.Result.GetProperty("ok").GetBoolean().Should().BeTrue();
        verified.Result.GetProperty("fromSeq").GetInt64().Should().Be(1, "a short chain fits the default window");

        await TamperAuditDetailAsync(seq: 3);
        var broken = await SendAsync(HttpMethod.Get, "/audit/verify?full=true", token: root);
        broken.Result.GetProperty("ok").GetBoolean().Should().BeFalse();
        broken.Result.GetProperty("firstBrokenSeq").GetInt64().Should().Be(3);
        (await SendAsync(HttpMethod.Get, "/audit/verify?full=true", token: root)).Code.Should().Be("RATE_LIMITED", "full rescans are throttled");
        (await SendAsync(HttpMethod.Get, "/audit/verify?fromSeq=5", token: root)).Result.GetProperty("ok").GetBoolean()
            .Should().BeTrue("a window anchored after the tampered row still verifies");
    }

    [Fact]
    public async Task Node_admin_tokens_are_rejected_on_every_non_node_admin_endpoint()
    {
        var root = await RootLoginAsync();

        // A plain [Authorize] user route: the root subject is not a Guid, so a fall-through would reach the
        // "no resolvable subject" early return and authenticate. The node-admin branch must fail it first (H6).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/wallet");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root);
        (await Client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // A mapped operator route admits the token, then the permission decides: root never holds kyc.read (M3).
        var overview = await SendToAsync(HttpMethod.Get, "/api/operator/overview", token: root);
        overview.Status.Should().Be(HttpStatusCode.Forbidden);
        overview.Code.Should().Be("PERMISSION_DENIED");

        // A legacy-only operator route (the legacy session revoke) refuses the token at validation (M7).
        using var legacyOnly = new HttpRequestMessage(HttpMethod.Post, "/api/operator/session/revoke");
        legacyOnly.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root);
        (await Client.SendAsync(legacyOnly)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await SendAsync(HttpMethod.Get, "/me", token: root)).Status.Should().Be(HttpStatusCode.OK);
    }

    private async Task TamperAuditDetailAsync(long seq)
    {
        var executor = new DefaultSurrealExecutor(new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = Factory.TestNamespace,
            Database = "test",
            User = SurrealTestDefaults.User,
            Password = SurrealTestDefaults.Password,
        }));
        var id = NodeAdminAuditLog.RecordIdFor(seq);
        var row = (await executor.QuerySingleAsync<NodeAdminAudit>(SurrealQuery<NodeAdminAudit>.Key(id), CancellationToken.None))!;
        (await executor.ExecuteAsync(SurrealWriter.DeleteOnly<NodeAdminAudit>(id).Where(item => item.Seq == seq).Build(), CancellationToken.None)).EnsureAllOk();
        row.Detail = "{\"tampered\":true}";
        (await executor.ExecuteAsync(SurrealWriter.Create(row), CancellationToken.None)).EnsureAllOk();
    }
}
