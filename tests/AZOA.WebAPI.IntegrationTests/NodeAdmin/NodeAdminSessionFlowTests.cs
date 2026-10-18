using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Session secrecy (M-1), protocol refusals, foreign credentials on node-admin routes, root revocation (L-2), audit coverage (M-3, L-3).</summary>
public sealed class NodeAdminSessionFlowTests(NodeAdminWebApplicationFactory factory)
    : NodeAdminHttpTestBase(factory), IClassFixture<NodeAdminWebApplicationFactory>
{
    private const string JwtKey = "node-admin-integration-test-signing-key-0123456789";

    [Fact]
    public async Task Listed_sessions_expose_handles_never_sids_and_a_handle_cannot_be_used_as_a_sid()
    {
        var root = await RootLoginAsync();
        var auditor = await CreateActiveAdminAsync(root, root, "aria", "auditor");
        var liveSids = new[] { SidOf(root), SidOf(auditor.Token) };

        var listed = await SendAsync(HttpMethod.Get, $"/sessions?principal=root:{Factory.RootKey.Fingerprint}", token: auditor.Token);
        listed.Status.Should().Be(HttpStatusCode.OK, listed.Body.ToString());
        var own = await SendAsync(HttpMethod.Get, "/me", token: auditor.Token);
        var exposed = AllStrings(listed.Body).Concat(AllStrings(own.Body)).ToArray();
        exposed.Should().NotIntersectWith(liveSids);
        own.Result.GetProperty("session").GetProperty("current").GetBoolean().Should().BeTrue();

        var handle = listed.Result.EnumerateArray().First().GetProperty("handle").GetString()!;
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(root).Claims.ToArray();
        string Revision(string type) => claims.Single(claim => claim.Type == type).Value;
        var revisions = new NodeAdminTokenRevisions(NodeAdminClaims.RootKeyRevision, long.Parse(Revision(NodeAdminClaims.RootKeyRevision)),
            NodeAdminClaims.RootSessionRevision, long.Parse(Revision(NodeAdminClaims.RootSessionRevision)));
        foreach (var sid in new[] { handle, NodeAdminCrypto.Base64Url(Guid.NewGuid().ToByteArray()) })
        {
            var forged = Issuer().Issue("root:" + Factory.RootKey.Fingerprint, sid, DateTimeOffset.UtcNow, ["ed25519", "root"], revisions,
                DateTimeOffset.UtcNow.AddMinutes(10));
            (await SendAsync(HttpMethod.Get, "/me", token: forged)).Status.Should().Be(HttpStatusCode.Unauthorized,
                "a token signed with Jwt:Key is useless without the sid of a live session");
        }
    }

    [Fact]
    public async Task Protocol_misuse_is_refused_and_anonymous_noise_stays_off_the_chain()
    {
        var root = await RootLoginAsync();
        var key = Factory.RootKey;

        var reused = await RequestChallengeAsync(key, "root_login");
        (await SignChallengeAsync(reused, key, null, null, "root_login")).Status.Should().Be(HttpStatusCode.OK);
        (await SignChallengeAsync(reused, key, null, null, "root_login")).Code.Should().Be("AUTH_FAILED", "a challenge is single use");
        (await LoginAsync(key, "root_login", null, signAudience: "https://evil.example.test")).Code.Should().Be("AUTH_FAILED");
        (await LoginAsync(key, "admin_login", null, signPurpose: "root_login")).Code.Should().Be("AUTH_FAILED", "the purpose is part of the signed string");
        (await LoginAsync(key, "admin_login", null)).Code.Should().Be("AUTH_FAILED", "a root key is not an admin credential");
        (await SendAsync(HttpMethod.Post, "/auth/challenge", new { purpose = "enroll", fingerprint = key.Fingerprint })).Code.Should().Be("VALIDATION_FAILED");

        var headBefore = (await SendAsync(HttpMethod.Get, "/audit?limit=1", token: root)).Result.GetProperty("head").GetProperty("seq").GetInt64();
        (await SendAsync(HttpMethod.Post, "/auth/session", new { challengeId = "nope", publicKey = "x", signature = "y" })).Code.Should().Be("AUTH_FAILED");
        (await SendAsync(HttpMethod.Post, "/auth/session", new { challengeId = Guid.NewGuid().ToString("D"), publicKey = key.PublicKey, signature = key.Sign("x") }))
            .Code.Should().Be("AUTH_FAILED");
        (await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken = "unknown", publicKey = key.PublicKey, label = "x" })).Code.Should().Be("ENROLLMENT_INVALID");
        (await SendAsync(HttpMethod.Post, "/enroll/complete", new { enrollmentId = Guid.NewGuid().ToString("D"), totp = "123456", signature = key.Sign("x") }))
            .Code.Should().Be("ENROLLMENT_INVALID");
        var stranger = NodeAdminTestKey.Generate();
        var strangerChallenge = await RequestChallengeAsync(stranger, "admin_login");
        (await SendAsync(HttpMethod.Post, "/auth/session", new
        {
            challengeId = strangerChallenge.GetProperty("challengeId").GetString(),
            publicKey = stranger.PublicKey,
            signature = stranger.Sign("not the challenge"),
        })).Code.Should().Be("AUTH_FAILED", "a bad signature from a key nobody registered is rolled-up noise (NEW-3)");
        var headAfter = (await SendAsync(HttpMethod.Get, "/audit?limit=1", token: root)).Result.GetProperty("head").GetProperty("seq").GetInt64();
        headAfter.Should().Be(headBefore, "failures tied to no real challenge or enrollment are rolled up, not chained (M-3)");
    }

    [Fact]
    public async Task User_tokens_api_keys_and_invalid_bodies_are_handled_at_the_node_admin_boundary()
    {
        var root = await RootLoginAsync();

        (await SendAsync(HttpMethod.Get, "/me", token: UserToken(Guid.NewGuid().ToString("D")))).Status.Should().Be(HttpStatusCode.Unauthorized);
        var service = await SendAsync(HttpMethod.Get, "/me", token: UserToken("service"));
        service.Status.Should().Be(HttpStatusCode.Forbidden, "an authenticated non-node-admin principal never satisfies a NodeAdmin policy");
        using (var apiKey = new HttpRequestMessage(HttpMethod.Get, Base + "/admins"))
        {
            apiKey.Headers.Add("X-Api-Key", "azoa_" + new string('a', 40));
            (await Client.SendAsync(apiKey)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // L-3: an invalid body on a node-admin write still leaves an audit outcome row.
        (await SendAsync(HttpMethod.Post, "/admins", token: root, rawJson: "{\"username\":\"zed\",\"displayName\":\"Z\",\"roleIds\":\"node-owner\"}"))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await AuditRowsAsync(root)).Should().Contain(row => row.GetProperty("action").GetString() == "POST /api/node-admin/admins"
            && row.GetProperty("outcome").GetString() == "failed" && row.GetProperty("detail").GetString()!.Contains("400"));
    }

    [Fact]
    public async Task Only_a_root_session_can_revoke_root_sessions_so_a_rogue_owner_cannot_lock_root_out()
    {
        var first = await RootLoginAsync();
        var second = await RootLoginAsync();
        var owner = await CreateActiveAdminAsync(first, first, "rhea", "node-owner");

        (await SendAsync(HttpMethod.Post, "/root/revoke-sessions", token: owner.Token)).Code.Should().Be("PERMISSION_DENIED");
        var rootSessions = await SendAsync(HttpMethod.Get, $"/sessions?principal=root:{Factory.RootKey.Fingerprint}", token: owner.Token);
        var rootHandle = rootSessions.Result.EnumerateArray().First().GetProperty("handle").GetString()!;
        (await SendAsync(HttpMethod.Post, $"/sessions/{rootHandle}/revoke", token: owner.Token)).Code.Should().Be("PERMISSION_DENIED");
        (await SendAsync(HttpMethod.Get, "/me", token: first)).Status.Should().Be(HttpStatusCode.OK, "refused revocations leave root signed in");

        (await SendAsync(HttpMethod.Post, "/root/revoke-sessions", token: second)).Status.Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, "/me", token: first)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/me", token: second)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/me", token: owner.Token)).Status.Should().Be(HttpStatusCode.OK, "admin sessions are unaffected");
        var rows = await AuditRowsAsync(owner.Token);
        rows.Should().Contain(row => row.GetProperty("action").GetString() == "POST /api/node-admin/root/revoke-sessions"
            && row.GetProperty("actor").GetString() == "admin:" + owner.Id && row.GetProperty("outcome").GetString() == "denied");
        rows.Should().Contain(row => row.GetProperty("action").GetString() == "POST /api/node-admin/sessions/{handle}/revoke"
            && row.GetProperty("outcome").GetString() == "denied");
        rows.Should().Contain(row => row.GetProperty("action").GetString() == "POST /api/node-admin/root/revoke-sessions"
            && row.GetProperty("actor").GetString()!.StartsWith("root:") && row.GetProperty("outcome").GetString() == "ok");
    }

    private static NodeAdminTokenIssuer Issuer() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Key"] = JwtKey,
        ["Jwt:Issuer"] = "test",
        ["Jwt:Audience"] = "test",
    }).Build());

    private static string UserToken(string subject)
    {
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken("test", "test",
            [new Claim("sub", subject), new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
             new Claim(AzoaClaims.AuthTime, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
            now.UtcDateTime, now.AddMinutes(10).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IEnumerable<string> AllStrings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => AllStrings(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(AllStrings),
        _ => [],
    };
}
