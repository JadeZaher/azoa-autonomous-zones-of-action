using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyb;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Kyb;

public sealed class IdentomatKybTests
{
    [Fact]
    public async Task UnsentBudgetRefusalReleasesAdmissionForSafeRetry()
    {
        using var fixture = new Fixture();
        fixture.AllowBudget = false;
        (await fixture.Service.BeginAsync(fixture.TenantId, "request")).IsError.Should().BeTrue();
        fixture.Http.BeginCount.Should().Be(0);
        fixture.AllowBudget = true;
        (await fixture.Service.BeginAsync(fixture.TenantId, "request")).IsError.Should().BeFalse();
        fixture.Http.BeginCount.Should().Be(1);
    }

    [Fact]
    public async Task AmbiguousBeginRemainsVisibleAsOperatorRecoveryDuringPolling()
    {
        using var fixture = new Fixture();
        fixture.Http.LoseBeginResponse = true;
        (await fixture.Service.BeginAsync(fixture.TenantId, "request")).IsError.Should().BeTrue();
        (await fixture.Service.GetStatusAsync(fixture.TenantId)).Result!.Status.Should().Be("PENDING");
        (await fixture.Service.RefreshAsync(fixture.TenantId)).Result!.Status.Should().Be("PENDING");
        fixture.Http.BeginCount.Should().Be(1);
    }
    internal const string Approved = """
        {"result":{"stepsResults":[
          {"type":"company","status":"approved","result":{"companyName":"Example Company","registrationNumber":"123","registrationCountry":"US"}},
          {"type":"beneficiaries","status":"approved","result":[{"entityType":"individual","positions":["shareholder"],"_id":"participant-1","sessionIds":["child-1"],"firstName":"PRIVATE-NAME","sessionUrl":"https://widget.identomat.com/?session_token=PRIVATE-TOKEN"}]},
          {"type":"kyb-review","status":"approved"}
        ]}}
        """;

    [Fact]
    public async Task RealAdapterAndFlow_WithMockHttp_StoresBusinessAndLinksWithoutBeneficiaryPii()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.BeginAsync(fixture.TenantId, "stable-request");
        first.IsError.Should().BeFalse();
        first.Result!.Verification.Status.Should().Be("PENDING");
        var retry = await fixture.Service.BeginAsync(fixture.TenantId, "new-browser-request");
        retry.Result!.Resumed.Should().BeTrue();
        fixture.Http.BeginCount.Should().Be(1);
        fixture.Http.BeginBody!.Value.GetProperty("configId").GetString().Should().Be("business-config");
        fixture.Http.BeginBody.Value.GetProperty("clientUserId").GetString().Should().Be(fixture.Store.Current!.AdmissionKey);

        await fixture.Service.HandleCallbackAsync(fixture.Callback("rejected"));
        var status = await fixture.Service.GetStatusAsync(fixture.TenantId);
        status.Result!.Verified.Should().BeTrue("callback claims are ignored in favor of the authoritative HTTP result");
        status.Result.BeneficiaryCount.Should().Be(1);
        fixture.Store.Current!.BusinessEvidenceJson.Should().Contain("child-1").And.NotContain("PRIVATE-NAME").And.NotContain("PRIVATE-TOKEN");
        JsonSerializer.Serialize(status).Should().NotContain("child-1").And.NotContain("provider-parent");
        fixture.Http.ResultBody!.Value.GetProperty("sessionId").GetString().Should().Be("provider-parent");
        fixture.Http.ResultBody.Value.TryGetProperty("session_token", out _).Should().BeFalse();

        await fixture.Service.HandleCallbackAsync(fixture.Callback("approved"));
        fixture.Store.Decisions.Should().Be(1);
    }

    [Fact]
    public async Task CallbackSecretAndCurrentTenantAuthorityAreRequired()
    {
        using var fixture = new Fixture();
        await fixture.Service.BeginAsync(fixture.TenantId, "request");
        await fixture.Service.HandleCallbackAsync("""{"callbackApiKey":"wrong","sessionId":"provider-parent","result":"approved"}""");
        fixture.Http.ResultCount.Should().Be(0);
        fixture.ActiveUser.IsActive = false;
        await fixture.Service.HandleCallbackAsync(fixture.Callback("approved"));
        fixture.Store.Decisions.Should().Be(0);
        (await fixture.Service.GetStatusAsync(fixture.TenantId)).Code.Should().Be(AzoaErrorCodes.Forbidden);
    }

    [Fact]
    public async Task ChangedPolicyRejectsPreviouslyCreatedSession()
    {
        using var fixture = new Fixture();
        await fixture.Service.BeginAsync(fixture.TenantId, "request");
        fixture.Policy.PolicyVersion = "replacement-policy";
        await fixture.Service.HandleCallbackAsync(fixture.Callback("approved"));
        fixture.Store.Decisions.Should().Be(0);
        (await fixture.Service.GetStatusAsync(fixture.TenantId)).Result!.Verified.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticatedRefreshUsesOwnedSessionAndRealAdapter()
    {
        using var fixture = new Fixture();
        await fixture.Service.BeginAsync(fixture.TenantId, "request");
        var result = await fixture.Service.RefreshAsync(fixture.TenantId);
        result.Result!.Verified.Should().BeTrue();
        fixture.Http.ResultCount.Should().Be(1);
        var foreign = await fixture.Service.GetStatusAsync(Guid.NewGuid());
        foreign.Code.Should().Be(AzoaErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("beneficiaries")]
    [InlineData("kyb-review")]
    public void MissingCriticalStepCannotApprove(string omitted)
    {
        using var document = JsonDocument.Parse(Approved);
        var steps = document.RootElement.GetProperty("result").GetProperty("stepsResults").EnumerateArray()
            .Where(step => step.GetProperty("type").GetString() != omitted).ToArray();
        var result = JsonSerializer.SerializeToElement(new { result = new { stepsResults = steps } });
        IdentomatKybResultParser.Parse(result).IsTerminal.Should().BeFalse();
    }

    [Theory]
    [InlineData("manual-check")]
    [InlineData("new-vendor-state")]
    [InlineData("APPROVED")]
    public void UnknownAndManualStatesCannotApprove(string status)
    {
        using var document = JsonDocument.Parse(Approved.Replace("\"status\":\"approved\"", $"\"status\":\"{status}\""));
        IdentomatKybResultParser.Parse(document.RootElement).IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void DocumentedSingularParticipantSessionIsSupportedWithoutOptionalMetadata()
    {
        using var document = JsonDocument.Parse(Approved.Replace("\"sessionIds\":[\"child-1\"]", "\"sessionId\":\"child-1\"")
            .Replace("\"positions\":[\"shareholder\"],", "").Replace("\"_id\":\"participant-1\",", ""));
        var outcome = IdentomatKybResultParser.Parse(document.RootElement);
        outcome.IsApproved.Should().BeTrue();
        outcome.Business!.Beneficiaries.Single().SessionIds.Should().Equal("child-1");
    }

    [Fact]
    public void ConflictingSingularAndPluralParticipantSessionsCannotApprove()
    {
        using var document = JsonDocument.Parse(Approved.Replace("\"sessionIds\":[\"child-1\"]", "\"sessionIds\":[\"child-1\"],\"sessionId\":\"different\""));
        IdentomatKybResultParser.Parse(document.RootElement).IsApproved.Should().BeFalse();
    }

    [Fact]
    public void MissingParticipantVerificationLinkCannotApprove()
    {
        using var document = JsonDocument.Parse(Approved.Replace("\"sessionIds\":[\"child-1\"],", ""));
        IdentomatKybResultParser.Parse(document.RootElement).IsApproved.Should().BeFalse();
    }

    [Fact]
    public async Task MissingBusinessWorkflowNeverCallsVendor()
    {
        using var fixture = new Fixture();
        fixture.Policy.ConfigId = null;
        fixture.Service.GetCapabilities().Available.Should().BeFalse();
        (await fixture.Service.BeginAsync(fixture.TenantId, "request")).IsError.Should().BeTrue();
        fixture.Http.BeginCount.Should().Be(0);
    }

    private sealed class Fixture : IDisposable
    {
        public Guid TenantId { get; } = Guid.NewGuid();
        public KybSettings Policy { get; } = new() { ConfigId = "business-config", PolicyVersion = "v1" };
        public AZOA.WebAPI.Models.User ActiveUser { get; } = new() { IsActive = true };
        public FixtureHttpHandler Http { get; } = new();
        public MemoryBusinessStore Store { get; }
        public KybService Service { get; }
        public bool AllowBudget { get; set; } = true;
        private readonly HttpClient _client;
        private readonly string _callbackKey;

        public Fixture()
        {
            var settings = IdentomatTestFactory.Options();
            _callbackKey = settings.Value.Identomat.CallbackApiKey!;
            _client = new HttpClient(Http);
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(_client);
            var budget = new Mock<IIdentomatRateBudget>();
            budget.Setup(value => value.TryAcquire(It.IsAny<string>())).Returns(() => AllowBudget);
            var transport = new IdentomatHttpTransport(clients.Object, budget.Object, settings);
            var provider = new IdentomatKybProvider(settings, Options.Create(Policy), transport);
            var sessions = new InMemoryHostedSessionStore();
            Store = new MemoryBusinessStore(sessions);
            var users = new Mock<IUserStore>();
            users.Setup(value => value.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IUser>.Success(null!));
            users.Setup(value => value.GetByIdAsync(TenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IUser>.Success(ActiveUser));
            var keys = new Mock<IApiKeyStore>();
            keys.Setup(value => value.ListByUserAsync(TenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AZOA.WebAPI.Models.ApiKey> { new() { UserId = TenantId, Scopes = "tenant:provision" } });
            var handler = new KybHostedSessionOutcomeHandler(Store, provider, Options.Create(Policy), TimeProvider.System, users.Object, keys.Object);
            var flow = new HostedSessionFlow(sessions, handler);
            Service = new KybService(Store, provider, flow, IdentomatTestFactory.AlwaysAllowQuota(), Options.Create(Policy),
                TimeProvider.System, users.Object, keys.Object, sessions);
        }

        public string Callback(string untrusted) => JsonSerializer.Serialize(new { callbackApiKey = _callbackKey, sessionId = "provider-parent", result = untrusted });
        public void Dispose() => _client.Dispose();
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public bool LoseBeginResponse { get; set; }
        public int BeginCount { get; private set; }
        public int ResultCount { get; private set; }
        public JsonElement? BeginBody { get; private set; }
        public JsonElement? ResultBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath == "/begin")
            {
                BeginCount++;
                if (LoseBeginResponse) throw new HttpRequestException("Response lost after provider acceptance.");
                BeginBody = document.RootElement.Clone();
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"provider-parent","url":"https://widget.identomat.com/?session_token=provider-parent"}""") };
            }
            request.RequestUri.AbsolutePath.Should().Be("/result");
            ResultCount++;
            ResultBody = document.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = new StringContent(Approved) };
        }
    }

    private sealed class MemoryBusinessStore(InMemoryHostedSessionStore sessions) : IKybVerificationStore
    {
        public KybVerification? Current { get; private set; }
        public int Decisions { get; private set; }
        public Task<KybVerification?> GetAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(Current?.Id == tenantId.ToString("N") ? Clone(Current) : null);
        public Task<KybVerification?> TryStartAsync(KybVerification? expected, KybVerification candidate, CancellationToken ct = default)
        {
            if (Current?.Version != expected?.Version) return Task.FromResult<KybVerification?>(null);
            Current = Clone(candidate);
            return Task.FromResult(Clone(Current));
        }
        public Task<bool> AttachSessionAsync(Guid tenantId, string attemptId, Guid sessionId, CancellationToken ct = default)
        {
            if (Current?.Id != tenantId.ToString("N") || Current.AttemptId != attemptId) return Task.FromResult(false);
            Current.HostedSessionId = sessionId.ToString("N");
            return Task.FromResult(true);
        }
        public async Task<KybVerification?> TryDecideAsync(KybVerification expected, HostedSession session, string status, string? evidence,
            DateTimeOffset reviewedAt, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            var lease = await sessions.GetByIdAsync(Guid.Parse(session.Id), ct);
            if (Current?.Version != expected.Version || Current.Status != "PENDING" || Current.ExpiresAt <= reviewedAt
                || lease.Result?.ResumeLeaseToken != session.ResumeLeaseToken || lease.Result?.ResumeLeaseExpiresAt <= reviewedAt)
                return null;
            Current.Status = status;
            Current.BusinessEvidenceJson = evidence;
            Current.ReviewedAt = reviewedAt;
            Current.ExpiresAt = expiresAt;
            Current.Version++;
            Decisions++;
            return Clone(Current);
        }
        private static KybVerification? Clone(KybVerification? value)
            => value is null ? null : JsonSerializer.Deserialize<KybVerification>(JsonSerializer.Serialize(value));
    }
}
