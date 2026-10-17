using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealConsentLifecycleTests
{
    [Theory]
    [InlineData("claimed")]
    [InlineData("offboarded")]
    [InlineData("revoked")]
    [InlineData("unchanged")]
    public async Task Custodial_kyc_mutations_and_first_send_reject_changed_authority(string change)
    {
        var provisioning = new SigningConsentWitness(1, Authority() with { ActorUserId = _tenant }, [AzoaScopes.TenantProvision], []);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
            .Set(value => value.OwnerTenantId, "user:" + _tenant.ToString("N")).Set(value => value.ExternalUserId, "kyc-child").Build())).EnsureAllOk();
        var enrolled = (await _store.AdmitProvisionedAsync(_actor, "kyc-child", provisioning))!;
        var access = new TenantCustodialAccess(_executor);
        var captured = (await access.CaptureAsync(_actor, "kyc-child",
            provisioning with { RequiredScopes = [AzoaScopes.TenantProvision, AzoaScopes.KycSubmit] }, AzoaScopes.KycSubmit))!;
        captured.Should().NotBeNull();
        var now = DateTimeOffset.UtcNow;
        (await _executor.ExecuteAsync(SurrealWriter.Create(new KycProviderProfile { Id = "manual", DisplayName = "Manual",
            AdapterKey = "manual", Enabled = true, PolicyVersion = "v1", AssuranceLevel = "manual", TrustRevision = 1,
            UpdatedByUserId = "user:" + _tenant.ToString("N"), UpdatedAt = now }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new TenantKycProviderSelection { Id = _tenant.ToString("N"),
            TenantId = "user:" + _tenant.ToString("N"), ProviderKey = "manual", SelectionVersion = 1,
            UpdatedByUserId = "user:" + _tenant.ToString("N"), UpdatedAt = now }))).EnsureAllOk();
        var id = Guid.NewGuid();
        var attempt = new KycSubmission { Id = id.ToString("N"), UserId = _actor.ToString("N"), TenantId = _tenant.ToString("N"),
            Provider = KycProvider.MANUAL, ProviderKey = "manual", ProviderSelectionVersion = 1, ProviderTrustRevision = 1,
            ProviderSessionId = id.ToString("N"), Status = KycStatus.PENDING, HostedVerification = true,
            SubmittedAt = now, CreatedDate = now, ModifiedDate = now, ExpiresAt = now.AddHours(1) };
        var kyc = new SurrealKycStore(_executor);
        (await kyc.CreateCustodialSubmissionAsync(attempt, [], captured)).IsError.Should().BeFalse();
        var sessions = new SurrealHostedSessionStore(_executor);
        var candidate = new HostedSession { Id = id.ToString("N"), FlowKind = HostedSessionFlowKinds.Kyc,
            ProviderKey = "manual", AdmissionKey = "kyc-attempt:" + id.ToString("N"), CreationToken = Guid.NewGuid().ToString("N"),
            SubjectUserId = _actor.ToString("N"), TenantId = _tenant.ToString("N"), State = HostedSessionStates.Claimed,
            CreatedDate = now, ModifiedDate = now, CreationClaimedAt = now };
        (await sessions.ClaimAdmissionAsync(candidate)).IsError.Should().BeFalse();
        var document = new KycDocument { Id = Guid.NewGuid().ToString("N"), SubmissionId = id.ToString("N"),
            Type = KycDocumentType.PASSPORT, FileName = "passport.pdf", FileUrl = "https://documents.example/passport", CreatedDate = now };
        if (change == "unchanged")
        {
            (await sessions.TryBeginCustodialCreationAsync(id, candidate.CreationToken!, now, captured)).Result.Should().NotBeNull();
            (await sessions.TryBeginCustodialCreationAsync(id, candidate.CreationToken!, now, captured)).Result.Should().BeNull();
            (await kyc.AttachCustodialDocumentsAsync(attempt, [document], captured)).IsError.Should().BeFalse();
            (await kyc.GetDocumentsBySubmissionAsync(id)).Result.Should().ContainSingle();
            return;
        }
        if (change == "claimed")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N"))
                .Unset(value => value.OwnerTenantId).Set(value => value.AuthNotBefore, now.AddSeconds(1)).Build())).EnsureAllOk();
        else if (change == "offboarded")
            (await _store.OffboardAsync(_tenant, enrolled.ParticipationRef!, provisioning)).Should().NotBeNull();
        else (await _store.RevokeUserExplicitAsync(enrolled.GrantId, Authority())).Should().NotBeNull();

        (await kyc.AttachCustodialDocumentsAsync(attempt, [document], captured)).IsError.Should().BeTrue();
        (await kyc.RetireCustodialAttemptAsync(attempt, now.AddMinutes(1), captured)).IsError.Should().BeTrue();
        (await sessions.TryBeginCustodialCreationAsync(id, candidate.CreationToken!, now, captured)).Result.Should().BeNull();
        (await sessions.GetByIdAsync(id)).Result!.CreationStartedAt.Should().BeNull();
        (await kyc.GetSubmissionByIdAsync(id)).Result!.Status.Should().Be(KycStatus.PENDING);
        (await kyc.GetDocumentsBySubmissionAsync(id)).Result.Should().BeEmpty();
        attempt.Id = Guid.NewGuid().ToString("N");
        (await kyc.CreateCustodialSubmissionAsync(attempt, [], captured)).IsError.Should().BeTrue();
        (await _executor.QueryAsync<KycSubmission>(SurrealQuery<KycSubmission>.From())).Should().ContainSingle();
    }
}
