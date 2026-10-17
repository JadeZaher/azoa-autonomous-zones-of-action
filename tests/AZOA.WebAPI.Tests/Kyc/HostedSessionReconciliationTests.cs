using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class HostedSessionReconciliationTests
{
    [Fact]
    public void ReconciliationRequiresOperatorPolicy()
        => typeof(HostedSessionReconciliationController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Should().Contain(attribute => attribute.Policy == "RecentNodeOperatorSession");

    [Fact]
    public async Task ConfirmedAbsence_PreservesOperatorAuditAndReleasesAdmissionKey()
    {
        var store = new InMemoryHostedSessionStore();
        var actor = Guid.NewGuid();
        var candidate = NewCandidate();
        await store.ClaimAdmissionAsync(candidate);
        var controller = NewController(store, actor);

        var result = await controller.Reconcile(Guid.ParseExact(candidate.Id, "N"), new()
        {
            ConfirmedProviderAbsence = true,
            OperatorReviewedSubjectAndProvider = true,
            Reason = "Identomat support confirmed absence in support case 1234.",
        }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var recorded = store.Peek(Guid.ParseExact(candidate.Id, "N"))!;
        recorded.State.Should().Be(HostedSessionStates.Abandoned);
        recorded.ReconciledByUserId.Should().Be(actor.ToString("N"));
        recorded.ReconciliationReason.Should().Contain("case 1234");
        recorded.AdmissionKey.Should().NotBe(candidate.AdmissionKey);
        var replacement = NewCandidate();
        var claimed = await store.ClaimAdmissionAsync(replacement);
        claimed.Result!.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task SentRequest_AbsenceQuarantinesUntilOriginalResponseAttaches()
    {
        var store = new InMemoryHostedSessionStore();
        var candidate = NewCandidate();
        await store.ClaimAdmissionAsync(candidate);
        var id = Guid.ParseExact(candidate.Id, "N");
        var sent = await store.TryBeginCreationAsync(id, candidate.CreationToken!, DateTimeOffset.UtcNow);
        var quarantined = await store.ReconcileAdmissionAsync(sent.Result!, Guid.NewGuid(), "No session currently visible", null, DateTimeOffset.UtcNow);
        quarantined.Result!.State.Should().Be(HostedSessionStates.Claimed);
        quarantined.Result.AdmissionKey.Should().Be(candidate.AdmissionKey);
        (await store.ClaimAdmissionAsync(NewCandidate())).Result!.Id.Should().Be(candidate.Id);
        var attached = await store.TryAttachProviderSessionAsync(id, sent.Result!.StateVersion,
            "original-parent", "original-child", "https://widget.identomat.com/", null, candidate.CreationToken!);
        attached.Result!.ProviderSessionId.Should().Be("original-parent");
    }

    [Fact]
    public async Task UnsentRelease_FencesSuspendedCreatorBeforeItCanSend()
    {
        var store = new InMemoryHostedSessionStore();
        var candidate = NewCandidate();
        await store.ClaimAdmissionAsync(candidate);
        await store.ReconcileAdmissionAsync(candidate, Guid.NewGuid(), "Verified not sent", null, DateTimeOffset.UtcNow);
        var sent = await store.TryBeginCreationAsync(Guid.ParseExact(candidate.Id, "N"), candidate.CreationToken!, DateTimeOffset.UtcNow);
        sent.Result.Should().BeNull();
    }

    [Fact]
    public async Task LegacyUnknownSend_IsQuarantinedInsteadOfReleasingAdmission()
    {
        var store = new InMemoryHostedSessionStore();
        var candidate = NewCandidate();
        candidate.CreationToken = null;
        await store.ClaimAdmissionAsync(candidate);
        var result = await store.ReconcileAdmissionAsync(candidate, Guid.NewGuid(), "Provider currently reports absent", null, DateTimeOffset.UtcNow);
        result.Result!.State.Should().Be(HostedSessionStates.Claimed);
        result.Result.CreationQuarantinedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AbsenceWithoutReviewedEvidence_DoesNotReleaseAdmission()
    {
        var store = new InMemoryHostedSessionStore();
        var candidate = NewCandidate();
        await store.ClaimAdmissionAsync(candidate);

        var result = await NewController(store, Guid.NewGuid()).Reconcile(Guid.ParseExact(candidate.Id, "N"),
            new() { ConfirmedProviderAbsence = true, Reason = "Unverified assumption that no provider session exists." }, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        store.Peek(Guid.ParseExact(candidate.Id, "N"))!.State.Should().Be(HostedSessionStates.Claimed);
    }

    private static HostedSession NewCandidate() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProviderKey = "identomat",
        FlowKind = HostedSessionFlowKinds.Kyc,
        AdmissionKey = "reconciliation-test",
        CreationToken = Guid.NewGuid().ToString("N"),
        State = HostedSessionStates.Claimed,
        CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
    };

    private static HostedSessionReconciliationController NewController(InMemoryHostedSessionStore store, Guid actor)
        => new(store, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "Bearer")),
                },
            },
        };
}
