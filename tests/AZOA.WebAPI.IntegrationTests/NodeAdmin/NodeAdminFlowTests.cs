using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
public sealed class NodeAdminFlowTests(NodeAdminWebApplicationFactory factory) : IClassFixture<NodeAdminWebApplicationFactory>
{
    private const string Base = "/api/node-admin";
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Root_bootstraps_an_owner_who_enrolls_logs_in_with_totp_and_is_bound_by_rbac_invariants()
    {
        var root = await RootLoginAsync();
        var me = await SendAsync(HttpMethod.Get, "/me", token: root);
        me.Status.Should().Be(HttpStatusCode.OK);
        me.Result.GetProperty("principal").GetString().Should().Be("root:" + factory.RootKey.Fingerprint);
        Strings(me.Result.GetProperty("permissions")).Should().NotContain(["money.operate", "kyc.decide", "governance.write"]);

        // Root mints the first owner; the enrollment token is returned once.
        var created = await SendAsync(HttpMethod.Post, "/admins", new { username = "alice", displayName = "Alice", roleIds = new[] { "node-owner" } }, root);
        created.Status.Should().Be(HttpStatusCode.OK);
        var aliceId = created.Result.GetProperty("admin").GetProperty("id").GetString()!;
        created.Result.GetProperty("admin").GetProperty("createdBy").GetString().Should().Be("root:" + factory.RootKey.Fingerprint);
        var alice = await EnrollAsync(created.Result.GetProperty("enrollmentToken").GetString()!, expectAlreadyStartedOnRetry: true);

        // Activation confirms the enrolled key's fingerprint out of band.
        var wrong = await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/activate", new { fingerprint = "0000000000000000" }, root);
        wrong.Code.Should().Be("FINGERPRINT_MISMATCH");
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/activate", new { fingerprint = alice.Key.Fingerprint }, root))
            .Result.GetProperty("status").GetString().Should().Be("active");

        // TOTP login; the same code (same step) cannot be replayed.
        var code = alice.CurrentCode();
        var login = await AdminLoginAsync(alice.Key, code);
        login.Status.Should().Be(HttpStatusCode.OK);
        var aliceToken = login.Result.GetProperty("accessToken").GetString()!;
        Strings(login.Result.GetProperty("permissions")).Should().Contain("money.operate");
        (await AdminLoginAsync(alice.Key, code)).Code.Should().Be("AUTH_FAILED", "a TOTP step is single use");

        // Permitted calls for the owner: list admins and define a custom role.
        (await SendAsync(HttpMethod.Get, "/admins", token: aliceToken)).Status.Should().Be(HttpStatusCode.OK);
        var people = await SendAsync(HttpMethod.Post, "/roles", new { name = "people", permissions = new[] { "admins.read", "admins.manage", "roles.read" } }, aliceToken);
        people.Status.Should().Be(HttpStatusCode.OK);
        var peopleRoleId = people.Result.GetProperty("id").GetString()!;

        // A second admin with fewer permissions: enrolled, activated by the dominating owner, logged in.
        var bobCreated = await SendAsync(HttpMethod.Post, "/admins", new { username = "bob", displayName = "Bob", roleIds = new[] { peopleRoleId } }, aliceToken);
        var bobId = bobCreated.Result.GetProperty("admin").GetProperty("id").GetString()!;
        var bob = await EnrollAsync(bobCreated.Result.GetProperty("enrollmentToken").GetString()!, expectAlreadyStartedOnRetry: false);
        (await SendAsync(HttpMethod.Post, $"/admins/{bobId}/activate", new { fingerprint = bob.Key.Fingerprint }, aliceToken))
            .Status.Should().Be(HttpStatusCode.OK);
        var bobToken = (await AdminLoginAsync(bob.Key, bob.CurrentCode())).Result.GetProperty("accessToken").GetString()!;

        // Dominance, escalation and permission denials for the weaker admin.
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: bobToken)).Code.Should().Be("DOMINANCE_REQUIRED");
        (await SendAsync(HttpMethod.Post, "/admins", new { username = "mallory", displayName = "M", roleIds = new[] { "node-owner" } }, bobToken))
            .Code.Should().Be("ESCALATION_REFUSED");
        var denied = await SendAsync(HttpMethod.Get, "/audit", token: bobToken);
        denied.Status.Should().Be(HttpStatusCode.Forbidden);
        denied.Code.Should().Be("PERMISSION_DENIED");

        // Last owner: even root cannot suspend the only owner; nobody suspends themselves.
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: root)).Code.Should().Be("LAST_OWNER");
        (await SendAsync(HttpMethod.Post, $"/admins/{aliceId}/suspend", token: aliceToken)).Code.Should().Be("SELF_ACTION_REFUSED");
        (await SendAsync(HttpMethod.Put, $"/admins/{aliceId}/roles", new { roleIds = new[] { peopleRoleId } }, root)).Code.Should().Be("LAST_OWNER");

        // Revoking the session kills the token immediately (server-side session, C2).
        (await SendAsync(HttpMethod.Post, "/auth/revoke", token: aliceToken)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/me", token: aliceToken)).Status.Should().Be(HttpStatusCode.Unauthorized);

        // The audit names the human actors, records the denial and verifies; tampering one row breaks the chain there.
        var audit = await SendAsync(HttpMethod.Get, "/audit?afterSeq=0&limit=500", token: root);
        var rows = audit.Result.GetProperty("rows").EnumerateArray().ToArray();
        rows.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + bobId
            && row.GetProperty("action").GetString() == "authz.denied");
        rows.Should().Contain(row => row.GetProperty("actor").GetString() == "admin:" + aliceId
            && row.GetProperty("action").GetString() == "POST /api/node-admin/admins" && row.GetProperty("outcome").GetString() == "ok");
        rows.Should().Contain(row => row.GetProperty("action").GetString() == "auth.session" && row.GetProperty("outcome").GetString() == "failed");
        var verified = await SendAsync(HttpMethod.Get, "/audit/verify", token: root);
        verified.Result.GetProperty("ok").GetBoolean().Should().BeTrue();
        verified.Result.GetProperty("headHash").GetString().Should().Be(audit.Result.GetProperty("head").GetProperty("hash").GetString());

        await TamperAuditDetailAsync(seq: 3);
        var broken = await SendAsync(HttpMethod.Get, "/audit/verify", token: root);
        broken.Result.GetProperty("ok").GetBoolean().Should().BeFalse();
        broken.Result.GetProperty("firstBrokenSeq").GetInt64().Should().Be(3);
    }

    [Fact]
    public async Task Node_admin_tokens_are_rejected_on_every_non_node_admin_endpoint()
    {
        var root = await RootLoginAsync();

        // A plain [Authorize] user route: the root subject is not a Guid, so a fall-through would reach the
        // "no resolvable subject" early return and authenticate. The node-admin branch must fail it first (H6).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/wallet");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Operator routes do not accept node-admin tokens until lane A2 maps them.
        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, "/api/operator/overview");
        operatorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root);
        (await _client.SendAsync(operatorRequest)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The same token still works on its own surface.
        (await SendAsync(HttpMethod.Get, "/me", token: root)).Status.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> RootLoginAsync()
    {
        var session = await LoginAsync(factory.RootKey, "root_login", totp: null);
        session.Status.Should().Be(HttpStatusCode.OK, session.Body.ToString());
        return session.Result.GetProperty("accessToken").GetString()!;
    }

    private Task<Call> AdminLoginAsync(NodeAdminTestKey key, string totp) => LoginAsync(key, "admin_login", totp);

    private async Task<Call> LoginAsync(NodeAdminTestKey key, string purpose, string? totp)
    {
        var challenge = await SendAsync(HttpMethod.Post, "/auth/challenge", new { purpose, fingerprint = key.Fingerprint });
        challenge.Status.Should().Be(HttpStatusCode.OK, challenge.Body.ToString());
        var result = challenge.Result;
        var audience = result.GetProperty("audience").GetString()!;
        audience.Should().Be(NodeAdminWebApplicationFactory.Audience, "the client refuses any other audience (H1)");
        var message = string.Join('\n', "azoa-node-admin/v1", purpose, audience, result.GetProperty("challengeId").GetString(),
            result.GetProperty("nonce").GetString(), result.GetProperty("expiresAt").GetString());
        return await SendAsync(HttpMethod.Post, "/auth/session", new
        {
            challengeId = result.GetProperty("challengeId").GetString(),
            publicKey = key.PublicKey,
            signature = key.Sign(message),
            totp,
        });
    }

    private async Task<EnrolledAdmin> EnrollAsync(string enrollmentToken, bool expectAlreadyStartedOnRetry)
    {
        await AwaitStepHeadroomAsync();
        var key = NodeAdminTestKey.Generate();
        var start = await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken, publicKey = key.PublicKey, label = "laptop" });
        start.Status.Should().Be(HttpStatusCode.OK, start.Body.ToString());
        var secretBase32 = start.Result.GetProperty("totpSecretBase32").GetString()!;
        start.Result.GetProperty("otpauthUri").GetString().Should().Contain("secret=" + secretBase32).And.Contain("issuer=AZOA%20Node%20(node.example.test)");
        if (expectAlreadyStartedOnRetry)
        {
            var again = await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken, publicKey = NodeAdminTestKey.Generate().PublicKey, label = "x" });
            again.Code.Should().Be("ENROLLMENT_ALREADY_STARTED", "an enrollment binds exactly one key (H3)");
        }

        var enrolled = new EnrolledAdmin(key, FromBase32(secretBase32));
        var enrollmentId = start.Result.GetProperty("enrollmentId").GetString()!;
        var complete = await SendAsync(HttpMethod.Post, "/enroll/complete", new
        {
            enrollmentId,
            totp = enrolled.CodeAt(Totp.StepAt(DateTimeOffset.UtcNow) - 1),
            signature = key.Sign(string.Join('\n', "azoa-node-admin/v1", "enroll", NodeAdminWebApplicationFactory.Audience, enrollmentId)),
        });
        complete.Status.Should().Be(HttpStatusCode.OK, complete.Body.ToString());
        complete.Result.GetProperty("status").GetString().Should().Be("pending_activation");
        return enrolled;
    }

    private async Task TamperAuditDetailAsync(long seq)
    {
        var executor = new DefaultSurrealExecutor(new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = factory.TestNamespace,
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

    private async Task<Call> SendAsync(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method, Base + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Call(response.StatusCode, json);
    }

    /// <summary>Keeps a ±1-step TOTP sequence inside one 30 s window so the test is not flaky at a step boundary.</summary>
    private static async Task AwaitStepHeadroomAsync()
    {
        var intoStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % Totp.StepSeconds;
        if (intoStep > 22) await Task.Delay(TimeSpan.FromSeconds(Totp.StepSeconds - intoStep + 1));
    }

    private static IReadOnlyList<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var character in value)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits < 8) continue;
            bytes.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
        }
        return bytes.ToArray();
    }

    private sealed record Call(HttpStatusCode Status, JsonElement Body)
    {
        public JsonElement Result => Body.GetProperty("result");

        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record EnrolledAdmin(NodeAdminTestKey Key, byte[] Secret)
    {
        public string CodeAt(long step) => Totp.Code(Secret, step);

        public string CurrentCode() => CodeAt(Totp.StepAt(DateTimeOffset.UtcNow));
    }
}
