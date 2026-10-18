// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class IdentomatKycProviderServiceTests
{
    /// <summary>Must stay in lockstep with <see cref="IdentomatTestFactory.CompleteSettings"/>.</summary>
    private const string CallbackSecret = "callback-api-key-value-0123456789abcdef";

    // ── Capabilities ────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_WithCompleteConfiguration_IsAvailable()
    {
        var provider = IdentomatTestFactory.Create();

        var capabilities = provider.GetCapabilities();

        capabilities.Available.Should().BeTrue();
        capabilities.Provider.Should().Be(KycProvider.IDENTOMAT);
        capabilities.ProviderKey.Should().Be("identomat");
        capabilities.HostedVerification.Should().BeTrue();
        capabilities.AcceptsDocumentReferences.Should().BeFalse();
        capabilities.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void Capabilities_ReportMissingKeyNames_AndNeverTheValues()
    {
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.CompanyKey = null;
        settings.CallbackApiKey = null;
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings));

        var capabilities = provider.GetCapabilities();
        var missing = provider.MissingConfigurationKeys();

        capabilities.Available.Should().BeFalse();
        missing.Should().BeEquivalentTo(
            IdentomatKycSettings.CompanyKeyName,
            IdentomatKycSettings.CallbackApiKeyName);
        capabilities.UnavailableReason.Should().Contain(IdentomatKycSettings.CompanyKeyName);
        capabilities.UnavailableReason.Should().Contain(IdentomatKycSettings.CallbackApiKeyName);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("short-callback-secret")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void Capabilities_TreatAShortCallbackSecretAsUnconfigured(string secret)
    {
        // The callback secret is the ONLY authentication on an anonymous endpoint
        // rate-limited at 60 attempts per 60s. A presence check reports "test" as
        // fully configured; the floor mirrors KycPersonKeyDeriver's root-secret rule.
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.CallbackApiKey = secret;
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings));

        provider.GetCapabilities().Available.Should().BeFalse();
        provider.MissingConfigurationKeys()
            .Should().ContainSingle().Which.Should().Be(IdentomatKycSettings.CallbackApiKeyName);
        provider.GetCapabilities().UnavailableReason.Should().NotContain(secret);
    }

    [Fact]
    public void Capabilities_AcceptACallbackSecretAtExactlyTheFloor()
        => IdentomatKycProviderService
            .IsUsableCallbackSecret(new string('k', KycPersonKeyDeriver.MinimumSecretLength))
            .Should().BeTrue();

    [Theory]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://172.16.0.1")]
    public void Capabilities_RejectInternalAndLinkLocalOrigins(string origin)
    {
        // Operator configuration rather than attacker input, so this is hardening —
        // but 169.254.169.254 is a cloud metadata endpoint and the adapter POSTs a
        // company key at whatever origin it is handed.
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.BeginBaseUrl = origin;
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings));

        provider.MissingConfigurationKeys()
            .Should().Contain(IdentomatKycSettings.BeginBaseUrlName);
    }

    [Fact]
    public void Capabilities_NeverEchoAConfiguredSecretValue()
    {
        // Only the ConfigId is missing, so the reason string is built while three
        // real secret values are present in settings. None may appear in it.
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.ConfigId = null;
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings));

        var reason = provider.GetCapabilities().UnavailableReason;

        reason.Should().NotBeNull();
        reason.Should().NotContain(settings.CompanyKey!);
        reason.Should().NotContain(settings.CallbackApiKey!);
        reason.Should().Contain(IdentomatKycSettings.ConfigIdName);
    }

    [Fact]
    public void Capabilities_RejectNonHttpsOrigins()
    {
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.BeginBaseUrl = "http://external-api.identomat.com";
        settings.ResultBaseUrl = "https://127.0.0.1";
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings));

        provider.MissingConfigurationKeys().Should().BeEquivalentTo(
            IdentomatKycSettings.BeginBaseUrlName,
            IdentomatKycSettings.ResultBaseUrlName);
    }

    // ── Constant-time callback secret comparison ────────────────────────────

    [Fact]
    public void VerifyCallback_WithMatchingSecret_ReturnsCorrelationOnly()
    {
        var provider = IdentomatTestFactory.Create();

        var verified = provider.VerifyCallback(CallbackBody(CallbackSecret, "session-123", "APPROVED"));

        verified.IsError.Should().BeFalse();
        verified.Result!.ProviderSessionId.Should().Be("session-123");
        verified.Result.EventType.Should().Be("session-change");
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("callback-api-key-value-0123456789abcde")]  // one character short
    [InlineData("callback-api-key-value-0123456789abcdef ")] // trailing space on the candidate
    [InlineData("")]
    public void VerifyCallback_WithBadSecret_IsRejected(string candidate)
    {
        var provider = IdentomatTestFactory.Create();

        var verified = provider.VerifyCallback(CallbackBody(candidate, "session-123", "APPROVED"));

        verified.IsError.Should().BeTrue();
        verified.Result.Should().BeNull();
    }

    [Fact]
    public void VerifyCallback_RejectionMessage_NeverContainsTheSecret()
    {
        var provider = IdentomatTestFactory.Create();

        var verified = provider.VerifyCallback(CallbackBody("wrong-secret", "session-123", "APPROVED"));

        verified.Message.Should().NotContain(CallbackSecret);
        verified.Message.Should().NotContain("wrong-secret");
    }

    [Fact]
    public void VerifyCallback_MissingSessionId_IsRejectedEvenWithTheRightSecret()
    {
        var provider = IdentomatTestFactory.Create();

        var verified = provider.VerifyCallback(CallbackBody(CallbackSecret, string.Empty, "APPROVED"));

        verified.IsError.Should().BeTrue();
    }

    [Fact]
    public void VerifyCallback_MalformedJson_IsRejectedWithoutThrowing()
    {
        var provider = IdentomatTestFactory.Create();

        var verified = provider.VerifyCallback("{ not json");

        verified.IsError.Should().BeTrue();
    }

    [Fact]
    public void ConstantTimeEquals_IsLengthIndependentAndExact()
    {
        IdentomatKycProviderService.ConstantTimeEquals("abc", "abc").Should().BeTrue();
        IdentomatKycProviderService.ConstantTimeEquals("abc", "abd").Should().BeFalse();
        IdentomatKycProviderService.ConstantTimeEquals("abc", "abcdefghijklmnop").Should().BeFalse();
        IdentomatKycProviderService.ConstantTimeEquals(null, "abc").Should().BeFalse();
        IdentomatKycProviderService.ConstantTimeEquals("abc", null).Should().BeFalse();
    }

    // ── MANUAL_CHECK is non-terminal ────────────────────────────────────────

    [Fact]
    public void MapOutcome_ManualCheck_IsNonTerminalAndNotApproved()
    {
        var outcome = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse
        {
            Result = "MANUAL_CHECK",
        });

        outcome.IsTerminal.Should().BeFalse(
            "MANUAL_CHECK means a human is still deciding; treating it as terminal "
            + "would let an undecided identity satisfy a value gate");
        outcome.IsApproved.Should().BeFalse();
        outcome.Subject.Should().BeNull();
    }

    [Fact]
    public void MapStatus_ManualCheck_DoesNotProduceApproved()
    {
        var outcome = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse
        {
            Result = "manual_check",
            Person = new IdentomatPerson { DocumentNumber = "X1", IssuingState = "GE" },
        });

        IdentomatKycProviderService.MapStatus(outcome).Should().Be(KycStatus.IN_REVIEW);
    }

    [Theory]
    [InlineData("QUEUED")]
    [InlineData("IN_PROGRESS")]
    [InlineData("SOME_FUTURE_VENDOR_STATE")]
    [InlineData("")]
    [InlineData(null)]
    public void MapOutcome_UnknownVendorState_FailsClosedAsNonTerminal(string? raw)
    {
        var outcome = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse
        {
            Result = raw,
        });

        outcome.IsTerminal.Should().BeFalse();
        outcome.IsApproved.Should().BeFalse();
        IdentomatKycProviderService.MapStatus(outcome).Should().Be(KycStatus.IN_REVIEW);
    }

    [Fact]
    public void MapOutcome_Approved_CarriesOnlyTheFourRetainedFieldsPlusKeyInputs()
    {
        var outcome = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse
        {
            Result = "approved",
            Person = new IdentomatPerson
            {
                FirstName = "Ada",
                LastName = "Lovelace",
                Birthday = "1815-12-10",
                Citizenship = "GB",
                DocumentNumber = "AB-123 456",
                IssuingState = "GB",
            },
        });

        outcome.IsTerminal.Should().BeTrue();
        outcome.IsApproved.Should().BeTrue();
        outcome.Subject!.FirstName.Should().Be("Ada");
        outcome.Subject.Citizenship.Should().Be("GB");

        // Address, personal_number, and every media field have no property to land
        // in, so the shape itself is the guarantee.
        typeof(HostedSessionSubjectAttributes)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo(
                nameof(HostedSessionSubjectAttributes.FirstName),
                nameof(HostedSessionSubjectAttributes.LastName),
                nameof(HostedSessionSubjectAttributes.Birthday),
                nameof(HostedSessionSubjectAttributes.Citizenship),
                nameof(HostedSessionSubjectAttributes.DocumentNumber),
                nameof(HostedSessionSubjectAttributes.IssuingState));
    }

    [Fact]
    public void MapOutcome_Rejected_IsTerminalAndCarriesNoSubject()
    {
        var outcome = IdentomatKycProviderService.MapOutcome(new IdentomatResultResponse
        {
            Result = "rejected",
            RejectReason = new IdentomatRejectReason { Value = "DOCUMENT_EXPIRED" },
            Person = new IdentomatPerson { FirstName = "Ada", DocumentNumber = "X" },
        });

        outcome.IsTerminal.Should().BeTrue();
        outcome.IsApproved.Should().BeFalse();
        outcome.Detail.Should().Be("DOCUMENT_EXPIRED");
        outcome.Subject.Should().BeNull("a rejected verification asserts no identity");
    }

    // ── Begin session ───────────────────────────────────────────────────────

    [Fact]
    public async Task BeginSession_NeverReturnsTheProviderSessionIdToTheTenant()
    {
        // KycManager.ToSessionStart projects ProviderSessionId to the tenant, so the
        // adapter must put the AZOA session id there, never Identomat's.
        var azoaSessionId = Guid.NewGuid();
        var provider = IdentomatTestFactory.Create(
            flow: new StubFlow(AZOAResult<HostedSessionStartResult>.Success(new HostedSessionStartResult
            {
                SessionId = azoaSessionId,
                ProviderKey = "identomat",
                RedirectUrl = "https://widget.identomat.com/?session_token=child-abc",
            })));

        var started = await provider.BeginSessionAsync(Guid.NewGuid());

        started.IsError.Should().BeFalse();
        started.Result!.ProviderSessionId.Should().Be(azoaSessionId.ToString("N"));
        started.Result.ProviderSessionId.Should().NotContain("child-abc");
        started.Result.VerificationUrl.Should().Be("https://widget.identomat.com/?session_token=child-abc");
    }

    [Fact]
    public async Task BeginSession_WhenUnconfigured_FailsClosedWithoutCallingTheFlow()
    {
        var settings = IdentomatTestFactory.CompleteSettings();
        settings.CompanyKey = null;
        var flow = new StubFlow(AZOAResult<HostedSessionStartResult>.Failure("should not be reached"));
        var provider = IdentomatTestFactory.Create(IdentomatTestFactory.Options(settings), flow: flow);

        var started = await provider.BeginSessionAsync(Guid.NewGuid());

        started.IsError.Should().BeTrue();
        flow.BeginCallCount.Should().Be(0);
    }

    [Fact]
    public async Task BeginSession_WhenTenantQuotaIsExhausted_IsRefusedBeforeAnyExternalCall()
    {
        // Quota is spent at the send point inside the durable flow (a resumed session costs nothing),
        // so the observable contract is: typed refusal with Retry-After, no vendor call, and the
        // admission released so the next attempt can proceed.
        var quota = new Mock<IKycTenantQuota>();
        quota.Setup(item => item.TryConsume(It.IsAny<Guid?>(), KycQuotaOperation.BeginSession))
            .Returns(false);
        quota.SetupGet(item => item.RetryAfterSeconds).Returns(60);
        var transport = new Mock<IIdentomatHttpTransport>(MockBehavior.Strict);
        var store = new InMemoryHostedSessionStore();
        var flow = new AZOA.WebAPI.Services.HostedSessions.HostedSessionFlow(
            store, Mock.Of<AZOA.WebAPI.Services.HostedSessions.IHostedSessionOutcomeHandler>());
        var provider = IdentomatTestFactory.Create(transport: transport.Object, quota: quota.Object, flow: flow);
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        var started = await provider.BeginSessionAsync(user, tenant);

        started.IsError.Should().BeTrue();
        started.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        started.RetryAfterSeconds.Should().Be(60);
        var admission = await store.GetByAdmissionAsync(HostedSessionFlowKinds.Kyc,
            IdentomatKycProviderService.ProviderKeyValue, IdentomatKycProviderService.AdmissionKeyFor(user, tenant));
        admission.Result.Should().BeNull("an unsent admission is released under a resolved key");
    }

    [Fact]
    public void AdmissionKey_IsStablePerSubjectAndTenant()
    {
        var user = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        var first = IdentomatKycProviderService.AdmissionKeyFor(user, tenant);
        var second = IdentomatKycProviderService.AdmissionKeyFor(user, tenant);
        var direct = IdentomatKycProviderService.AdmissionKeyFor(user, null);
        var otherTenant = IdentomatKycProviderService.AdmissionKeyFor(user, Guid.NewGuid());

        first.Should().Be(second, "a retry must resume the same claim, not open a second session");
        first.Should().NotBe(direct);
        first.Should().NotBe(otherTenant);
    }

    // ── Document-reference paths stay closed ────────────────────────────────

    [Fact]
    public async Task DocumentReferencePaths_AreRefused()
    {
        var provider = IdentomatTestFactory.Create();

        var created = await provider.CreateSessionAsync(Guid.NewGuid(), []);
        var validated = await provider.ValidateDocumentsAsync([]);

        created.IsError.Should().BeTrue();
        validated.IsError.Should().BeTrue();
    }

    private static string CallbackBody(string apiKey, string sessionId, string result)
        => JsonSerializer.Serialize(new
        {
            callbackApiKey = apiKey,
            type = "session-change",
            sessionId,
            result,
        });
}
