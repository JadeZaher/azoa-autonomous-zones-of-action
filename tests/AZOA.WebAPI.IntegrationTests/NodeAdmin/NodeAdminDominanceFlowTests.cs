using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>
/// C1 dominance, MFA reset and the pending-admin role change (M-2) through HTTP. Bob manages people but holds no money
/// permissions; Carol holds money permissions, so Bob must be refused every mutation that targets her.
/// </summary>
public sealed class NodeAdminDominanceFlowTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    [Fact]
    public async Task A_people_manager_cannot_touch_an_admin_whose_permissions_they_lack()
    {
        var root = await RootLoginAsync();
        var alice = await CreateActiveAdminAsync(root, root, "alice", "node-owner");
        var people = await RoleAsync(alice.Token, "people", "admins.read", "admins.manage", "roles.read", "roles.manage", "audit.read");
        var readers = await RoleAsync(alice.Token, "readers", "admins.read", "roles.read");
        var bob = await CreateActiveAdminAsync(alice.Token, alice.Token, "bob", people);
        var carol = await CreateActiveAdminAsync(alice.Token, alice.Token, "carol", "operations", readers);

        (await SendAsync(HttpMethod.Put, $"/admins/{carol.Id}/roles", new { roleIds = new[] { readers } }, bob.Token))
            .Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Post, $"/admins/{carol.Id}/revoke-sessions", token: bob.Token)).Code.Should().Be("DOMINANCE_REQUIRED");
        var newKey = NodeAdminTestKey.Generate();
        (await SendAsync(HttpMethod.Post, $"/admins/{carol.Id}/keys", new { publicKey = newKey.PublicKey, label = "x", signature = newKey.Sign("x") }, bob.Token))
            .Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Delete, $"/admins/{carol.Id}/keys/{carol.Enrolled.Key.Fingerprint}", token: bob.Token))
            .Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Post, $"/admins/{carol.Id}/mfa/reset", token: bob.Token)).Code.Should().Be("DOMINANCE_REQUIRED");

        // Other-admin session revoke by opaque handle.
        var carolSessions = await SendAsync(HttpMethod.Get, $"/sessions?principal=admin:{carol.Id}", token: bob.Token);
        var carolHandle = carolSessions.Result.EnumerateArray().First().GetProperty("handle").GetString()!;
        (await SendAsync(HttpMethod.Post, $"/sessions/{carolHandle}/revoke", token: bob.Token)).Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Get, "/me", token: carol.Token)).Status.Should().Be(HttpStatusCode.OK, "a refused revoke changes nothing");

        // Editing a role Bob dominates still demotes Carol, whom he does not.
        (await SendAsync(HttpMethod.Put, $"/roles/{readers}", new { name = "readers", permissions = new[] { "admins.read" } }, bob.Token))
            .Code.Should().Be("DOMINANCE_REQUIRED");

        // Activation of a pending admin with money permissions.
        var (daveId, daveToken) = await CreateAdminAsync(alice.Token, "dave", "operations");
        var dave = await EnrollAsync(daveToken);
        (await SendAsync(HttpMethod.Post, $"/admins/{daveId}/activate", new { fingerprint = dave.Key.Fingerprint }, bob.Token))
            .Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Post, $"/admins/{daveId}/activate", new { fingerprint = dave.Key.Fingerprint }, alice.Token))
            .Status.Should().Be(HttpStatusCode.OK);

        // The owner may revoke Carol's session.
        (await SendAsync(HttpMethod.Post, $"/sessions/{carolHandle}/revoke", token: alice.Token)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/me", token: carol.Token)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mfa_reset_returns_a_new_enrollment_token_only_to_root_and_invalidates_older_ones()
    {
        var root = await RootLoginAsync();
        var owner = await CreateActiveAdminAsync(root, root, "olivia", "node-owner");
        var target = await CreateActiveAdminAsync(owner.Token, owner.Token, "tess", "auditor");

        var byOwner = await SendAsync(HttpMethod.Post, $"/admins/{target.Id}/mfa/reset", token: owner.Token);
        byOwner.Status.Should().Be(HttpStatusCode.OK, byOwner.Body.ToString());
        byOwner.Result.GetProperty("enrollmentToken").ValueKind.Should().Be(JsonValueKind.Null, "a peer owner never receives another admin's token (C1)");
        byOwner.Result.GetProperty("admin").GetProperty("status").GetString().Should().Be("pending_enrollment");
        (await SendAsync(HttpMethod.Get, "/me", token: target.Token)).Status.Should().Be(HttpStatusCode.Unauthorized);

        var first = (await SendAsync(HttpMethod.Post, $"/admins/{target.Id}/mfa/reset", token: root)).Result.GetProperty("enrollmentToken").GetString()!;
        var second = (await SendAsync(HttpMethod.Post, $"/admins/{target.Id}/mfa/reset", token: root)).Result.GetProperty("enrollmentToken").GetString()!;
        (await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = first, publicKey = NodeAdminTestKey.Generate().PublicKey, label = "x" }))
            .Code.Should().Be("ENROLLMENT_INVALID", "a later reset consumes earlier tokens");

        // Re-enrolling with the previous key is allowed; the second start on the same token is refused.
        var restart = await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = second, publicKey = target.Enrolled.Key.PublicKey, label = "again" });
        restart.Status.Should().Be(HttpStatusCode.OK, restart.Body.ToString());
        (await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = second, publicKey = NodeAdminTestKey.Generate().PublicKey, label = "x" }))
            .Code.Should().Be("ENROLLMENT_ALREADY_STARTED");
    }

    [Fact]
    public async Task Raising_a_pending_admins_roles_kills_the_creators_enrollment_token()
    {
        var root = await RootLoginAsync();
        var owner = await CreateActiveAdminAsync(root, root, "petra", "node-owner");
        var people = await RoleAsync(owner.Token, "staffing", "admins.read", "admins.manage", "roles.read", "audit.read", "governance.read",
            "kyc.read", "users.read", "money.read");
        var creator = await CreateActiveAdminAsync(owner.Token, owner.Token, "quinn", people);
        var (erinId, creatorToken) = await CreateAdminAsync(creator.Token, "erin", "auditor");

        var raised = await SendAsync(HttpMethod.Put, $"/admins/{erinId}/roles", new { roleIds = new[] { "operations" } }, owner.Token);
        raised.Status.Should().Be(HttpStatusCode.OK, raised.Body.ToString());
        raised.Result.GetProperty("enrollmentToken").ValueKind.Should().Be(JsonValueKind.Null);
        (await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = creatorToken, publicKey = NodeAdminTestKey.Generate().PublicKey, label = "x" }))
            .Code.Should().Be("ENROLLMENT_INVALID", "a token issued under the old roles must not enroll the raised admin (M-2)");

        var byRoot = await SendAsync(HttpMethod.Put, $"/admins/{erinId}/roles", new { roleIds = new[] { "operations" } }, root);
        var fresh = byRoot.Result.GetProperty("enrollmentToken").GetString()!;
        (await StartEnrollmentAsync(fresh)).Start.GetProperty("enrollmentId").GetString().Should().NotBeNullOrEmpty();
        (await AuditRowsAsync(root)).Should().Contain(row => row.GetProperty("action").GetString() == "admin.reenrollment_required");
    }

    private async Task<string> RoleAsync(string token, string name, params string[] permissions)
    {
        var role = await SendAsync(HttpMethod.Post, "/roles", new { name, permissions }, token);
        role.Status.Should().Be(HttpStatusCode.OK, role.Body.ToString());
        return role.Result.GetProperty("id").GetString()!;
    }
}
