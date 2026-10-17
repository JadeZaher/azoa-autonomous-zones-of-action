using System.Net;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class HostedIdentityProtocolTests
{
    [Fact]
    public async Task VeriffCallbackBeforeAttachment_RetriesTransientFailureWithoutAcknowledgingOrRepostingCreation()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => throw new HttpRequestException("creation response lost"));
        (await fixture.Begin(fixture.Veriff)).IsError.Should().BeTrue();
        var remote = Guid.NewGuid().ToString("D");
        var body = JsonSerializer.Serialize(new { verification = new { id = remote, vendorData = fixture.Id.ToString("N") } });
        var headers = new Dictionary<string, string> { ["X-AUTH-CLIENT"] = fixture.Settings.VeriffApiKey!,
            ["X-HMAC-SIGNATURE"] = HostedIdentityProtocol.Hmac(fixture.Settings.VeriffSharedSecret!, body) };
        var receipt = new HostedProviderCallback { Id = "callback", AttemptId = fixture.Id.ToString("N"), BodyDigest = HostedIdentityProtocol.Digest(body) };
        fixture.Attempts.Setup(store => store.AdmitCallbackAsync(It.IsAny<HostedProviderCallback>(), It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        fixture.Attempts.Setup(store => store.CompleteCallbackAsync(receipt.Id, It.IsAny<CancellationToken>()))
            .Callback(() => receipt.Processed = true).Returns(Task.CompletedTask);
        async Task<IActionResult> Deliver()
        {
            var http = new DefaultHttpContext();
            http.Request.Path = "/api/kyc/callback/veriff";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            foreach (var header in headers) http.Request.Headers[header.Key] = header.Value;
            return await new HostedIdentityCallbackController(fixture.Recovery)
                { ControllerContext = new ControllerContext { HttpContext = http } }.Callback(default);
        }
        fixture.Http.Enqueue(_ => throw new HttpRequestException("temporary decision outage"));
        (await Deliver()).Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(503);
        receipt.Processed.Should().BeFalse();
        (await fixture.Sessions.GetByIdAsync(fixture.Id)).Result!.ProviderSessionId.Should().BeNull();
        var decision = fixture.VeriffDecision(remote, fixture.User);
        var decisionText = await decision.Content.ReadAsStringAsync();
        fixture.Http.Enqueue(_ => fixture.SignedVeriff(JsonSerializer.Deserialize<JsonElement>(decisionText)));
        fixture.Http.Enqueue(_ => fixture.SignedVeriff(JsonSerializer.Deserialize<JsonElement>(decisionText)));
        (await Deliver()).Should().BeOfType<AcceptedResult>();
        receipt.Processed.Should().BeTrue();
        (await fixture.Sessions.GetByIdAsync(fixture.Id)).Result!.ProviderSessionId.Should().Be(remote);
        (await fixture.Recovery.CallbackAsync("veriff", body, headers, default)).Should().Be(HostedIdentityCallbackDisposition.Processed);
        fixture.Http.Requests.Count(request => request.Method == "POST").Should().Be(1);
    }

    [Fact]
    public void RetiredCallbackKeyWorksOnlyInsideExplicitBoundedGrace()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var retired = new HostedKycRetiredKey { KeyId = "previous", Secret = new string('p', 40), RetiredAt = now.AddHours(-1), AcceptUntil = now.AddHours(1) };
        fixture.Settings.Hosted.RetiredWebhookKeys.Add(retired);
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var body = JsonSerializer.Serialize(new { protocolVersion = "1", provider = "trusted", eventId = "rotation-event", attemptId = fixture.Id.ToString("N"), sessionId = "remote" });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-AZOA-Key-Id"] = "previous",
            ["X-AZOA-Timestamp"] = timestamp, ["X-AZOA-Signature"] = HostedIdentityProtocol.Hmac(retired.Secret, timestamp + "." + body) };
        fixture.Generic.VerifyCallback(body, headers).IsError.Should().BeFalse();
        retired.AcceptUntil = now.AddMinutes(-1);
        fixture.Generic.VerifyCallback(body, headers).IsError.Should().BeTrue();
        retired.AcceptUntil = retired.RetiredAt.AddHours(25);
        fixture.Generic.GetHostedCapabilities().Available.Should().BeFalse();
    }

    [Fact]
    public async Task VeriffLostCreation_IsNeverPostedAgain_AndTerminalCandidateMustMatchOriginalSubject()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => throw new HttpRequestException("lost response"));
        (await fixture.Begin(fixture.Veriff)).IsError.Should().BeTrue();
        var session = (await fixture.Sessions.GetByIdAsync(fixture.Id)).Result!;
        (await fixture.Veriff.RecoverCreationAsync(session, default)).IsError.Should().BeTrue();
        fixture.Http.Requests.Should().HaveCount(1);
        var remote = Guid.NewGuid().ToString("D");
        fixture.Http.Enqueue(_ => fixture.VeriffDecision(remote, Guid.NewGuid()));
        (await fixture.Veriff.RecoverCandidateAsync(session, remote, default)).IsError.Should().BeTrue();
        fixture.Attempt!.RemoteSessionId.Should().BeNull();
        fixture.Http.Enqueue(_ => fixture.VeriffDecision(remote, fixture.User));
        var recovered = await fixture.Veriff.RecoverCandidateAsync(session, remote, default);
        recovered.IsError.Should().BeFalse();
        recovered.Result!.ProviderSessionId.Should().Be(remote);
        recovered.Result.RedirectUrl.Should().Be("https://app.example/return");
        fixture.Http.Requests.Count(request => request.Method == "POST").Should().Be(1);
    }

    [Fact]
    public async Task VeriffUnsignedDecisionCannotApprove()
    {
        using var fixture = new Fixture();
        var remote = Guid.NewGuid().ToString("D");
        fixture.Http.Enqueue(_ => fixture.SignedVeriff(new { status = "success", verification = new { id = remote,
            vendorData = fixture.Id.ToString("N"), endUserId = fixture.User.ToString("D"), url = "https://magic.veriff.me/visit" } }, HttpStatusCode.Created));
        (await fixture.Begin(fixture.Veriff)).IsError.Should().BeFalse();
        fixture.Http.Enqueue(_ => { var response = fixture.VeriffDecision(remote, fixture.User); response.Headers.Remove("X-HMAC-SIGNATURE"); return response; });
        (await fixture.Veriff.FetchAuthoritativeOutcomeAsync(remote)).IsError.Should().BeTrue();
        fixture.Handler.Verify(handler => handler.ResumeAsync(It.IsAny<HostedSession>(), It.IsAny<HostedSessionOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("decisionId")]
    [InlineData("decidedAt")]
    [InlineData("verifiedAt")]
    [InlineData("validUntil")]
    [InlineData("subject")]
    [InlineData("evidencePayload")]
    [InlineData("evidenceDigest")]
    public async Task PendingOrReviewRejectsTerminalFieldsEvenWhenNull(string field)
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Creation()));
        (await fixture.Begin()).IsError.Should().BeFalse();
        foreach (var status in new[] { "pending", "review" })
        {
            var body = fixture.Result(status); body[field] = null;
            fixture.Http.Enqueue(_ => fixture.Json(body));
            (await fixture.Generic.FetchAuthoritativeOutcomeAsync("remote-session")).IsError.Should().BeTrue();
        }
    }

    [Fact]
    public async Task LostCreateResponse_ReplaysExactPersistedBytesAndKey_AfterReturnUrlChanges()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => throw new HttpRequestException("response lost"));
        var started = await fixture.Begin();
        started.IsError.Should().BeTrue();
        fixture.Settings.Hosted.ReturnUrl = "https://app.example/changed";
        fixture.Http.Enqueue(_ => new(HttpStatusCode.NotFound));
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Creation()));
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Result("pending")));
        var recovered = await fixture.Recovery.RecoverAsync(fixture.Attempt!, null, default);
        recovered.IsError.Should().BeFalse();
        recovered.Result!.State.Should().Be(HostedSessionStates.AwaitingOutcome);
        var posts = fixture.Http.Requests.Where(request => request.Method == "POST").ToArray();
        posts.Should().HaveCount(2);
        posts[1].Body.Should().Be(posts[0].Body);
        posts[1].Key.Should().Be(posts[0].Key).And.Be(fixture.Id.ToString("N"));
        posts[1].Body.Should().Contain("https://app.example/return");
        fixture.Handler.Verify(handler => handler.ResumeAsync(It.IsAny<HostedSession>(), It.IsAny<HostedSessionOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("tenant")]
    [InlineData("policy")]
    [InlineData("digest")]
    [InlineData("checks")]
    [InlineData("expiry")]
    public async Task ApprovalRequiresCorrelatedCompleteUnexpiredEvidence(string damage)
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Creation()));
        (await fixture.Begin()).IsError.Should().BeFalse();
        var result = fixture.Approval(damage);
        fixture.Http.Enqueue(_ => fixture.Json(result));
        var fetched = await fixture.Generic.FetchAuthoritativeOutcomeAsync("remote-session");
        fetched.IsError.Should().BeTrue();
        fixture.Attempts.Verify(store => store.PinTerminalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteEvidence_UsesSharedFlow_ContradictoryTerminalCannotReplacePin()
    {
        using var fixture = new Fixture();
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Creation()));
        (await fixture.Begin()).IsError.Should().BeFalse();
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Approval()));
        var applied = await fixture.Flow.RefreshAsync(fixture.Generic, fixture.Id);
        applied.IsError.Should().BeFalse();
        fixture.Handler.Verify(handler => handler.ResumeAsync(It.IsAny<HostedSession>(),
            It.Is<HostedSessionOutcome>(outcome => outcome.IsApproved && outcome.Subject!.DocumentNumber == "SYNTHETIC-DOC"), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Http.Enqueue(_ => fixture.Json(fixture.Approval(decision: "different-decision")));
        (await fixture.Generic.FetchAuthoritativeOutcomeAsync("remote-session")).IsError.Should().BeTrue();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("tampered")]
    public void CallbackRejectsInvalidKeyTimeOrSignature(string damage)
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var timestamp = (damage == "expired" ? now.AddMinutes(-6) : damage == "future" ? now.AddMinutes(6) : now).ToUnixTimeSeconds().ToString();
        var body = JsonSerializer.Serialize(new { protocolVersion = "1", provider = "trusted", eventId = "event-1", attemptId = fixture.Id.ToString("N"), sessionId = "remote-session" });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-AZOA-Key-Id"] = damage == "unknown" ? "other" : "current",
            ["X-AZOA-Timestamp"] = timestamp,
            ["X-AZOA-Signature"] = HostedIdentityProtocol.Hmac(fixture.Settings.Hosted.WebhookSecret!, timestamp + "." + body),
        };
        fixture.Generic.VerifyCallback(damage == "tampered" ? body.Replace("event-1", "event-2") : body, headers).IsError.Should().BeTrue();
    }

    [Fact]
    public void StrictParserRejectsDuplicateFieldsAndNonUtcTimestamps()
    {
        Action parse = () => HostedIdentityProtocol.Parse("{\"status\":\"pending\",\"status\":\"approved\"}");
        parse.Should().Throw<JsonException>();
        HostedIdentityProtocol.UtcInstant("2026-10-15T12:00:00", out _).Should().BeFalse();
        HostedIdentityProtocol.UtcInstant("2026-10-15", out _).Should().BeFalse();
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public KycSettings Settings { get; } = new()
        {
            VeriffApiKey = "veriff-integration", VeriffSharedSecret = new string('v', 40), VeriffBaseUrl = "https://stationapi.veriff.com",
            VeriffReturnUrl = "https://app.example/return",
            Hosted = new() { ProtocolVersion = "1", ProviderName = "trusted", BaseUrl = "https://verify.example",
                ApiKey = new string('a', 40), WebhookSecret = new string('b', 40), WebhookKeyId = "current",
                ReturnUrl = "https://app.example/return", RedirectOrigins = ["https://verify.example"] },
        };
        public Mock<IHostedProviderAttemptStore> Attempts { get; } = new(MockBehavior.Strict);
        public Mock<IHostedSessionOutcomeHandler> Handler { get; } = new(MockBehavior.Strict);
        public HostedProviderAttempt? Attempt { get; private set; }
        public InMemoryHostedSessionStore Sessions { get; } = new();
        public ScriptedHttp Http { get; } = new();
        public GenericHostedKycProviderService Generic { get; }
        public VeriffKycProviderService Veriff { get; }
        public HostedSessionFlow Flow { get; }
        public HostedIdentityRecovery Recovery { get; }
        private readonly HttpClient _client;
        private string? _pin;

        public Fixture()
        {
            _client = new HttpClient(Http);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(factory => factory.CreateClient(HostedIdentityHttp.ClientName)).Returns(_client);
            var transport = new HostedIdentityHttp(clients.Object);
            Generic = new(Options.Create(Settings), Attempts.Object, Sessions, transport);
            Veriff = new(Options.Create(Settings), Attempts.Object, Sessions, transport);
            Flow = new(Sessions, Handler.Object);
            Settings.ApprovalPolicy.PolicyVersion = "policy-1";
            var registry = new Mock<IKycProviderRegistry>();
            registry.Setup(item => item.ResolveTenantAsync(Tenant, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => AZOAResult<KycProviderResolution>.Success(new(Attempt?.AdapterKey == "veriff" ? Veriff : Generic, Settings, Tenant, 1, 1, "Trusted")));
            Recovery = new(Attempts.Object, Sessions, Flow, Veriff, Generic, registry.Object);
            Attempts.Setup(store => store.EnsureAsync(It.IsAny<HostedProviderAttempt>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HostedProviderAttempt row, CancellationToken _) => Attempt ??= row);
            Attempts.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Attempt);
            Attempts.Setup(store => store.GetByRemoteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Attempt);
            Attempts.Setup(store => store.ClaimAsync(It.IsAny<HostedSession>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HostedSession _, bool replay, DateTimeOffset now, CancellationToken _) => { Attempt!.SendStartedAt ??= now; return Attempt; });
            Attempts.Setup(store => store.SaveCreationAsync(It.IsAny<HostedProviderAttempt>(), It.IsAny<HostedSessionCreation>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HostedProviderAttempt row, HostedSessionCreation creation, CancellationToken _) =>
                { row.RemoteSessionId = creation.ProviderSessionId; row.RedirectUrl = creation.RedirectUrl; row.ExpiresAt = creation.ExpiresAt; return row; });
            Attempts.Setup(store => store.ScheduleAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Attempts.Setup(store => store.PinTerminalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, string decision, string digest, CancellationToken _) => { var identity = decision + digest; _pin ??= identity; return _pin == identity; });
            Handler.Setup(handler => handler.ResumeAsync(It.IsAny<HostedSession>(), It.IsAny<HostedSessionOutcome>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<string?>.Success("accepted"));
        }

        public Task<AZOAResult<HostedSessionStartResult>> Begin(IHostedSessionProvider? provider = null) => Flow.BeginAsync(provider ?? Generic, new()
        { SessionId = Id, SubjectUserId = User, TenantId = Tenant, AdmissionKey = Id.ToString("N"), PolicyRevision = "policy-1" });

        public Dictionary<string, object?> Result(string status) => new()
        {
            ["protocolVersion"] = "1", ["provider"] = "trusted", ["attemptId"] = Id.ToString("N"), ["subjectId"] = User.ToString("N"),
            ["tenantId"] = Tenant.ToString("N"), ["flow"] = "kyc", ["policyRevision"] = "policy-1", ["sessionId"] = "remote-session", ["status"] = status,
        };
        public Dictionary<string, object?> Creation()
        {
            var value = Result("pending"); value.Remove("status"); value["redirectUrl"] = "https://verify.example/visit";
            value["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"); return value;
        }
        public Dictionary<string, object?> Approval(string? damage = null, string decision = "decision-1")
        {
            var value = Result("approved");
            if (damage == "subject") value["subjectId"] = Guid.NewGuid().ToString("N");
            if (damage == "tenant") value["tenantId"] = Guid.NewGuid().ToString("N");
            if (damage == "policy") value["policyRevision"] = "other";
            value["decisionId"] = decision; value["decidedAt"] = DateTimeOffset.UtcNow.ToString("O");
            value["verifiedAt"] = Attempt!.CreatedAt.ToString("O");
            value["validUntil"] = (damage == "expiry" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(1)).ToString("O");
            value["subject"] = new { firstName = "Synthetic", lastName = "Person", birthDate = "1990-01-01", issuingCountry = "US", documentNumber = "SYNTHETIC-DOC" };
            var evidence = new Dictionary<string, object?>(value) { ["checks"] = new { documentVerified = true, biometricMatchVerified = true, livenessVerified = damage != "checks" } };
            var payload = JsonSerializer.Serialize(evidence);
            value["evidencePayload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            value["evidenceDigest"] = damage == "digest" ? new string('0', 64) : HostedIdentityProtocol.Digest(payload);
            return value;
        }
        public HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        public HttpResponseMessage SignedVeriff(object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var text = JsonSerializer.Serialize(body);
            var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            response.Headers.Add("X-HMAC-SIGNATURE", HostedIdentityProtocol.Hmac(Settings.VeriffSharedSecret!, text));
            return response;
        }
        public HttpResponseMessage VeriffDecision(string remote, Guid subject) => SignedVeriff(new
        {
            status = "success", verification = new { id = remote, vendorData = Id.ToString("N"), endUserId = subject.ToString("D"),
                code = 9001, status = "approved", attemptId = "bfb69178-a7ca-44ec-b0d5-3ebfa165a533", decisionTime = DateTimeOffset.UtcNow.ToString("O"),
                person = new { firstName = "Synthetic", lastName = "Person", dateOfBirth = "1990-01-01" },
                document = new { country = "US", number = "SYNTHETIC-DOC", validUntil = "2035-01-01" } },
        });
        public void Dispose() => _client.Dispose();
    }

    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<(string Method, string Body, string? Key)> Requests { get; } = [];
        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, body, request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));
            return _responses.Dequeue()(request);
        }
    }
}
