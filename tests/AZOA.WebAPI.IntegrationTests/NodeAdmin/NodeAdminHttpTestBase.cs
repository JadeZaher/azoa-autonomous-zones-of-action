using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.NodeAdmin;

/// <summary>Client-side helpers that behave like the CLI: real Ed25519 signatures over the server's strings, real TOTP codes.</summary>
public abstract class NodeAdminHttpTestBase(NodeAdminWebApplicationFactory factory)
{
    protected const string Base = "/api/node-admin";
    protected readonly NodeAdminWebApplicationFactory Factory = factory;
    protected readonly HttpClient Client = factory.CreateClient();

    protected async Task<string> RootLoginAsync()
    {
        var session = await LoginAsync(Factory.RootKey, "root_login", totp: null);
        session.Status.Should().Be(HttpStatusCode.OK, session.Body.ToString());
        return session.Result.GetProperty("accessToken").GetString()!;
    }

    protected Task<Call> AdminLoginAsync(NodeAdminTestKey key, string totp) => LoginAsync(key, "admin_login", totp);

    protected async Task<Call> LoginAsync(NodeAdminTestKey key, string purpose, string? totp, string? signAudience = null, string? signPurpose = null)
    {
        var challenge = await RequestChallengeAsync(key, purpose);
        return await SignChallengeAsync(challenge, key, totp, signAudience, signPurpose ?? purpose);
    }

    protected async Task<JsonElement> RequestChallengeAsync(NodeAdminTestKey key, string purpose)
    {
        var challenge = await SendAsync(HttpMethod.Post, "/auth/challenge", new { purpose, fingerprint = key.Fingerprint });
        challenge.Status.Should().Be(HttpStatusCode.OK, challenge.Body.ToString());
        challenge.Result.GetProperty("audience").GetString().Should().Be(NodeAdminWebApplicationFactory.Audience,
            "the client refuses any other audience (H1)");
        return challenge.Result;
    }

    protected Task<Call> SignChallengeAsync(JsonElement challenge, NodeAdminTestKey key, string? totp, string? signAudience, string signPurpose)
    {
        var message = string.Join('\n', "azoa-node-admin/v1", signPurpose, signAudience ?? challenge.GetProperty("audience").GetString(),
            challenge.GetProperty("challengeId").GetString(), challenge.GetProperty("nonce").GetString(), challenge.GetProperty("expiresAt").GetString());
        return SendAsync(HttpMethod.Post, "/auth/session", new
        {
            challengeId = challenge.GetProperty("challengeId").GetString(),
            publicKey = key.PublicKey,
            signature = key.Sign(message),
            totp,
        });
    }

    /// <summary>Creates an admin as <paramref name="creatorToken"/>; returns its wire id and one-time token.</summary>
    protected async Task<(string Id, string EnrollmentToken)> CreateAdminAsync(string creatorToken, string username, params string[] roleIds)
    {
        var created = await SendAsync(HttpMethod.Post, "/admins", new { username, displayName = username, roleIds }, creatorToken);
        created.Status.Should().Be(HttpStatusCode.OK, created.Body.ToString());
        return (created.Result.GetProperty("admin").GetProperty("id").GetString()!, created.Result.GetProperty("enrollmentToken").GetString()!);
    }

    /// <summary>Enrolls with a fresh key; the admin ends in pending_activation.</summary>
    protected async Task<EnrolledAdmin> EnrollAsync(string enrollmentToken)
    {
        var started = await StartEnrollmentAsync(enrollmentToken);
        return await CompleteEnrollmentAsync(started.Key, started.Start);
    }

    protected async Task<(NodeAdminTestKey Key, JsonElement Start)> StartEnrollmentAsync(string enrollmentToken)
    {
        await AwaitStepHeadroomAsync();
        var key = NodeAdminTestKey.Generate();
        var start = await SendAsync(HttpMethod.Post, "/enroll/start", new { enrollmentToken, publicKey = key.PublicKey, label = "laptop" });
        start.Status.Should().Be(HttpStatusCode.OK, start.Body.ToString());
        return (key, start.Result);
    }

    protected async Task<EnrolledAdmin> CompleteEnrollmentAsync(NodeAdminTestKey key, JsonElement start)
    {
        var enrolled = new EnrolledAdmin(key, FromBase32(start.GetProperty("totpSecretBase32").GetString()!));
        var enrollmentId = start.GetProperty("enrollmentId").GetString()!;
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

    /// <summary>Create → enroll → activate (by <paramref name="activatorToken"/>) → login; returns id, enrolled key/secret and a session token.</summary>
    protected async Task<ActiveAdmin> CreateActiveAdminAsync(string creatorToken, string activatorToken, string username, params string[] roleIds)
    {
        var (id, enrollmentToken) = await CreateAdminAsync(creatorToken, username, roleIds);
        var enrolled = await EnrollAsync(enrollmentToken);
        (await SendAsync(HttpMethod.Post, $"/admins/{id}/activate", new { fingerprint = enrolled.Key.Fingerprint }, activatorToken))
            .Status.Should().Be(HttpStatusCode.OK);
        var login = await AdminLoginAsync(enrolled.Key, enrolled.CurrentCode());
        login.Status.Should().Be(HttpStatusCode.OK, login.Body.ToString());
        return new ActiveAdmin(id, enrolled, login.Result.GetProperty("accessToken").GetString()!);
    }

    protected Task<Call> SendAsync(HttpMethod method, string path, object? body = null, string? token = null, string? rawJson = null)
        => SendToAsync(method, Base + path, body, token, rawJson);

    /// <summary>Sends to any route (not only <c>api/node-admin</c>), e.g. an operator route a node admin is mapped onto.</summary>
    protected async Task<Call> SendToAsync(HttpMethod method, string absolutePath, object? body = null, string? token = null, string? rawJson = null)
    {
        using var request = new HttpRequestMessage(method, absolutePath);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (rawJson is not null) request.Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Call(response.StatusCode, json,
            response.Headers.TryGetValues("x-azoa-auth-requirement", out var requirement) ? requirement.SingleOrDefault() : null);
    }

    protected async Task<IReadOnlyList<JsonElement>> AuditRowsAsync(string readerToken)
    {
        var audit = await SendAsync(HttpMethod.Get, "/audit?afterSeq=0&limit=500", token: readerToken);
        audit.Status.Should().Be(HttpStatusCode.OK, audit.Body.ToString());
        return audit.Result.GetProperty("rows").EnumerateArray().ToArray();
    }

    protected static string SidOf(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(claim => claim.Type == "sid").Value;

    /// <summary>Keeps a ±1-step TOTP sequence inside one 30 s window so the test is not flaky at a step boundary.</summary>
    protected static async Task AwaitStepHeadroomAsync()
    {
        var intoStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % Totp.StepSeconds;
        if (intoStep > 22) await Task.Delay(TimeSpan.FromSeconds(Totp.StepSeconds - intoStep + 1));
    }

    protected static IReadOnlyList<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

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

    protected sealed record Call(HttpStatusCode Status, JsonElement Body, string? AuthRequirement = null)
    {
        public JsonElement Result => Body.GetProperty("result");

        public string? Code => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    protected sealed record EnrolledAdmin(NodeAdminTestKey Key, byte[] Secret)
    {
        public string CodeAt(long step) => Totp.Code(Secret, step);

        public string CurrentCode() => CodeAt(Totp.StepAt(DateTimeOffset.UtcNow));
    }

    protected sealed record ActiveAdmin(string Id, EnrolledAdmin Enrolled, string Token);
}
