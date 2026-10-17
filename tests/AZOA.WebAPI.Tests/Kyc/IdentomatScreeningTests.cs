// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class IdentomatScreeningTests
{
    [Fact]
    public async Task Screening_persists_the_authoritative_attempt_lineage()
    {
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnSearch(new IdentomatScreeningSearchResponse { Result = [] });
        var result = await NewService(transport, store).ScreenAsync(
            Guid.NewGuid(), null, "John Doe", null, lineageKey: "attempt-current");
        result.Result!.LineageKey.Should().Be("attempt-current");
        store.SavedResults.Should().ContainSingle().Which.LineageKey.Should().Be("attempt-current");
    }

    [Fact]
    public async Task Same_subject_new_attempt_gets_its_own_local_monitor_lineage()
    {
        var store = new InMemoryKycScreeningStore();
        var user = Guid.NewGuid();
        store.SeedMonitor("John Doe", "", "monitor-shared", subjectUserId: user, lineageKey: "attempt-old");
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse
        {
            ArgumentError = IdentomatScreeningService.AlreadyExistsArgumentError,
        });
        var result = await NewService(transport, store).RegisterMonitorAsync(
            user, null, "John Doe", null, lineageKey: "attempt-current");
        result.IsError.Should().BeFalse();
        result.Result!.ProviderMonitorId.Should().Be("monitor-shared");
        result.Result.LineageKey.Should().Be("attempt-current");
        store.SavedMonitors.Should().HaveCount(2);
    }

    [Fact]
    public async Task Poll_preserves_each_attempt_lineage_when_fanning_out_shared_monitor()
    {
        var store = new InMemoryKycScreeningStore();
        var user = Guid.NewGuid();
        store.SeedMonitor("John Doe", "", "m1", subjectUserId: user, lineageKey: "old");
        store.SeedMonitor("John Doe", "", "m1", subjectUserId: user, lineageKey: "current");
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 1, ("m1", "2026-08-20T12:00:00Z")));
        transport.OnSearch(new IdentomatScreeningSearchResponse { Result = [] });
        var result = await NewService(transport, store).PollMonitoringAsync();
        result.IsError.Should().BeFalse();
        store.SavedResults.Select(row => row.LineageKey).Should().BeEquivalentTo("old", "current");
    }

    // ── Monitor registration: already-exists is benign ──────────────────────

    [Fact]
    public async Task MonitorRegistration_AlreadyExists_IsIdempotentSuccessNotFailure()
    {
        // The vendor's own dedup guard. Treating it as an error would make a retry
        // look like a broken integration and could unregister a live monitor.
        var store = new InMemoryKycScreeningStore();
        var existing = store.SeedMonitor("John Doe", "1980-11-07T13:07:59.790Z", "monitor-abc");
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse
        {
            ArgumentError = IdentomatScreeningService.AlreadyExistsArgumentError,
        });
        var service = NewService(transport, store);

        var registered = await service.RegisterMonitorAsync(
            Guid.NewGuid(), null, "John Doe", "1980-11-07T13:07:59.790Z");

        registered.IsError.Should().BeFalse("already-exists is the vendor confirming the monitor is live");
        registered.Result!.ProviderMonitorId.Should().Be(existing.ProviderMonitorId);
        registered.Result.RegisteredIdempotently.Should().BeTrue();
    }

    [Fact]
    public async Task MonitorRegistration_AlreadyExistsWithNoLocalRecord_SurfacesAReconciliationConflict()
    {
        // The upstream monitor exists but we hold no id for it, so we could never
        // remove it later. Inventing an id would be worse than reporting it.
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse
        {
            ArgumentError = IdentomatScreeningService.AlreadyExistsArgumentError,
        });
        var service = NewService(transport, new InMemoryKycScreeningStore());

        var registered = await service.RegisterMonitorAsync(
            Guid.NewGuid(), null, "John Doe", null);

        registered.IsError.Should().BeTrue();
        registered.Code.Should().Be(AzoaErrorCodes.Conflict);
    }

    [Fact]
    public async Task MonitorRegistration_PersistsTheExactQueryShapeItRegisteredWith()
    {
        // The vendor keys a monitor on the query SHAPE, not a person id, so the
        // stored fields must be exactly what was sent.
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse { Result = "monitor-xyz" });
        var service = NewService(transport, store);

        await service.RegisterMonitorAsync(
            Guid.NewGuid(), null, "  John Doe  ", " 1980-11-07T13:07:59.790Z ");

        var saved = store.SavedMonitors.Should().ContainSingle().Subject;
        saved.QueryFullName.Should().Be("John Doe");
        saved.QueryBirthdayTimeFrom.Should().Be("1980-11-07T13:07:59.790Z");
        saved.ProviderMonitorId.Should().Be("monitor-xyz");
        saved.State.Should().Be(KycMonitorState.ACTIVE);

        var sent = transport.LastMonitorRegisterRequest!;
        sent.Query.FullName.Should().Be("John Doe");
        sent.Query.BirthdayTimeFrom.Should().Be("1980-11-07T13:07:59.790Z");
        sent.Query.ScoreThreshold.Should().BeNull(
            "monitor-screening-query accepts no scoreThreshold; sending one invents a field");
    }

    [Fact]
    public async Task MonitorRegistration_WithAnUnrelatedArgumentError_Fails()
    {
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse
        {
            ArgumentError = "invalid-query",
        });
        var service = NewService(transport, new InMemoryKycScreeningStore());

        var registered = await service.RegisterMonitorAsync(Guid.NewGuid(), null, "John Doe", null);

        registered.IsError.Should().BeTrue();
        registered.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
    }

    // ── Screening persists the threshold it queried with ────────────────────

    [Fact]
    public async Task Screen_PersistsTheScoreThresholdItQueriedWith()
    {
        // The vendor applies the threshold server-side and never echoes it per hit,
        // so without this the stored row cannot be explained later.
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnSearch(new IdentomatScreeningSearchResponse { Result = [] });
        var service = NewService(transport, store, scoreThreshold: 72);

        await service.ScreenAsync(Guid.NewGuid(), null, "John Doe", null);

        var saved = store.SavedResults.Should().ContainSingle().Subject;
        saved.ScoreThreshold.Should().Be(72);
        saved.Decision.Should().Be(KycScreeningDecision.NO_HIT);
        saved.PolicyVersion.Should().Be(KycScreeningRiskPolicy.CurrentPolicyVersion);
        transport.LastSearchRequest!.Query.ScoreThreshold.Should().Be(72);
    }

    [Fact]
    public async Task Screen_WhenTenantQuotaIsExhausted_MakesNoProviderCall()
    {
        var transport = new FakeTransport();
        var quota = new Moq.Mock<IKycTenantQuota>();
        quota.Setup(item => item.TryConsume(Moq.It.IsAny<Guid?>(), KycQuotaOperation.Screening))
            .Returns(false);
        quota.SetupGet(item => item.RetryAfterSeconds).Returns(60);
        var service = NewService(transport, new InMemoryKycScreeningStore(), quota: quota.Object);

        var screened = await service.ScreenAsync(Guid.NewGuid(), Guid.NewGuid(), "John Doe", null);

        screened.IsError.Should().BeTrue();
        screened.Code.Should().Be(AzoaErrorCodes.OperationNotAllowed);
        transport.CallCount.Should().Be(0);
    }

    // ── Risk policy is ours, derived from score + open topic set ────────────

    [Fact]
    public void RiskPolicy_NoHits_IsNoHit()
    {
        var assessment = NewPolicy().Assess([]);

        assessment.Decision.Should().Be(KycScreeningDecision.NO_HIT);
        assessment.TopScore.Should().Be(0);
        assessment.HitCount.Should().Be(0);
    }

    [Fact]
    public void RiskPolicy_BlockingTopic_BlocksAtAnyScore()
    {
        // The score measures identity-match confidence, not listing severity, so a
        // sanctions topic must block even at a modest match score.
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit
            {
                Score = 81,
                PersonId = "p1",
                Topics = ["Sanctioned entity"],
            },
        ]);

        assessment.Decision.Should().Be(KycScreeningDecision.HIT);
        assessment.BlockingTopicsMatched.Should().ContainSingle().Which.Should().Be("Sanctioned entity");
    }

    [Fact]
    public void RiskPolicy_HighScoreWithoutABlockingTopic_StillBlocks()
    {
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit { Score = 99, PersonId = "p1", Topics = ["Close associate"] },
        ]);

        assessment.Decision.Should().Be(KycScreeningDecision.HIT);
        assessment.TopScore.Should().Be(99);
    }

    [Fact]
    public void RiskPolicy_ModerateScoreWithANonBlockingTopic_GoesToManualReview()
    {
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit
            {
                Score = 85,
                PersonId = "p1",
                Topics = ["Politician (Politically Exposed Persons)"],
            },
        ]);

        assessment.Decision.Should().Be(KycScreeningDecision.MANUAL_REVIEW,
            "a PEP match is a human decision, not an automatic block");
    }

    [Fact]
    public void RiskPolicy_TreatsTopicsAsAnOpenStringSet()
    {
        // A topic the vendor has never published must flow through unchanged rather
        // than blowing up an enum parse.
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit
            {
                Score = 82,
                PersonId = "p1",
                Topics = ["Some Entirely New Vendor Topic", "Another Unpublished Category"],
            },
        ]);

        assessment.Decision.Should().Be(KycScreeningDecision.MANUAL_REVIEW);
        assessment.Topics.Should().Contain("Some Entirely New Vendor Topic");
    }

    [Fact]
    public void RiskPolicy_MatchesBlockingTopicsCaseInsensitively()
    {
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit { Score = 80, PersonId = "p1", Topics = ["sanctioned ENTITY"] },
        ]);

        assessment.Decision.Should().Be(KycScreeningDecision.HIT);
    }

    [Fact]
    public void RiskPolicy_UnionsTopicsAndPersonIdsAcrossHits()
    {
        var assessment = NewPolicy().Assess(
        [
            new IdentomatScreeningHit { Score = 81, PersonId = "p1", Topics = ["Close associate"] },
            new IdentomatScreeningHit { Score = 88, PersonId = "p2", Topics = ["Person of Interest", "Close associate"] },
        ]);

        assessment.HitCount.Should().Be(2);
        assessment.TopScore.Should().Be(88);
        assessment.PersonIds.Should().BeEquivalentTo("p1", "p2");
        assessment.Topics.Should().BeEquivalentTo("Close associate", "Person of Interest");
    }

    [Fact]
    public void ScreeningDecision_IsNotAVendorEnum()
    {
        // The vendor publishes no APPROVED/REJECTED on screening; these three values
        // are entirely AZOA's decision surface.
        Enum.GetNames<KycScreeningDecision>().Should().BeEquivalentTo("NO_HIT", "MANUAL_REVIEW", "HIT");
    }

    // ── Cold poll: batched, high-water-marked, follow-up on change ──────────

    [Fact]
    public async Task Poll_UsesOneBatchedListCallPerPage_NotOnePerMonitoredIdentity()
    {
        var store = new InMemoryKycScreeningStore();
        store.SeedMonitor("A A", string.Empty, "m1");
        store.SeedMonitor("B B", string.Empty, "m2");
        store.SeedMonitor("C C", string.Empty, "m3");
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 3,
            ("m1", "2026-08-20T00:00:00Z"),
            ("m2", "2026-08-20T00:00:00Z"),
            ("m3", "2026-08-20T00:00:00Z")));
        transport.OnSearch(new IdentomatScreeningSearchResponse { Result = [] });
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.IsError.Should().BeFalse();
        polled.Result!.RecordsExamined.Should().Be(3);
        transport.MonitorListCallCount.Should().Be(1,
            "three monitored identities must cost ONE list call, not three; the "
            + "per-IP budget is shared with live KYC traffic");
    }

    [Fact]
    public async Task Poll_FollowsUpOnlyOnRecordsWhoseTimestampAdvanced()
    {
        // The list call returns no match detail, so a changed timestamp is the only
        // signal and the follow-up fetch is what actually learns the new state.
        var store = new InMemoryKycScreeningStore();
        store.SeedMonitor("A A", string.Empty, "m1", lastObserved: "2026-08-01T00:00:00Z");
        store.SeedMonitor("B B", string.Empty, "m2", lastObserved: "2026-08-01T00:00:00Z");
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 2,
            ("m1", "2026-08-20T00:00:00Z"),
            ("m2", "2026-08-01T00:00:00Z")));
        transport.OnSearch(new IdentomatScreeningSearchResponse
        {
            Result = [new IdentomatScreeningHit { Score = 99, PersonId = "p1", Topics = ["Wanted person"] }],
        });
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.Result!.RecordsChanged.Should().Be(1);
        polled.Result.FollowUpsPerformed.Should().Be(1);
        transport.SearchCallCount.Should().Be(1);
        store.SavedResults.Should().ContainSingle()
            .Which.Decision.Should().Be(KycScreeningDecision.HIT);
    }

    [Fact]
    public async Task Poll_WhenParkedOnBackoff_MakesNoProviderCall()
    {
        var store = new InMemoryKycScreeningStore
        {
            PollState = new KycScreeningPollState
            {
                Id = KycScreeningPollState.LocalId,
                BackoffUntil = DateTimeOffset.UtcNow.AddMinutes(10),
            },
        };
        var transport = new FakeTransport();
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.Result!.StoppedOnBudget.Should().BeTrue();
        transport.CallCount.Should().Be(0,
            "retrying into an active ban is what earns the one-hour IP lockout");
    }

    [Fact]
    public async Task Poll_WhenTheListCallFails_RecordsAFailureAndBacksOff()
    {
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport { FailMonitorList = true };
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.Result!.StoppedOnBudget.Should().BeTrue();
        store.PollFailureCount.Should().Be(1);
        store.PollSuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Poll_AdvancesTheHighWaterMark()
    {
        var store = new InMemoryKycScreeningStore();
        store.SeedMonitor("A A", string.Empty, "m1", lastObserved: "2026-08-01T00:00:00Z");
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 1, ("m1", "2026-08-20T12:00:00Z")));
        transport.OnSearch(new IdentomatScreeningSearchResponse { Result = [] });
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.Result!.NextHighWaterMarkDate.Should().Be("2026-08-20T12:00:00Z");
        store.PollSuccessCount.Should().Be(1);
    }

    [Theory]
    [InlineData(null, "2026-08-20T00:00:00Z", true)]
    [InlineData("2026-08-01T00:00:00Z", "2026-08-20T00:00:00Z", true)]
    [InlineData("2026-08-20T00:00:00Z", "2026-08-20T00:00:00Z", false)]
    [InlineData("2026-08-20T00:00:00Z", "2026-08-01T00:00:00Z", false)]
    [InlineData("2026-08-20T00:00:00Z", null, false)]
    public void HasAdvanced_DetectsOnlyForwardMovement(
        string? lastObserved,
        string? candidate,
        bool expected)
        => IdentomatScreeningService.HasAdvanced(lastObserved, candidate).Should().Be(expected);

    // ── Cross-tenant isolation: the monitor key is a name and a birthday ────

    [Fact]
    public async Task MonitorRegistration_AlreadyExists_NeverReturnsAnotherTenantsRow()
    {
        // The vendor dedups the query SHAPE across the whole company key, so tenant B
        // registering the same person gets `already-exists` even though B has no row.
        // Handing back A's row would hand over A's subject user and A's tenant id —
        // the third instance of this repo leaking cross-tenant through a shared key.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var store = new InMemoryKycScreeningStore();
        var seeded = store.SeedMonitor(
            "John Doe", "1980-11-07T13:07:59.790Z", "monitor-abc",
            tenantId: tenantA, subjectUserId: userA);
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse
        {
            ArgumentError = IdentomatScreeningService.AlreadyExistsArgumentError,
        });
        var service = NewService(transport, store);

        var registered = await service.RegisterMonitorAsync(
            Guid.NewGuid(), tenantB, "John Doe", "1980-11-07T13:07:59.790Z");

        registered.IsError.Should().BeFalse(registered.Message);
        registered.Result!.Id.Should().NotBe(seeded.Id, "tenant B must get its OWN row");
        registered.Result.TenantScope.Should().Be(tenantB.ToString("N"));
        registered.Result.SubjectUserId.Should().NotBe(userA.ToString("N"),
            "tenant A's subject user must never be projected into tenant B's answer");
        registered.Result.TenantId.Should().Be(tenantB.ToString("N"));
        registered.Result.ProviderMonitorId.Should().Be("monitor-abc",
            "one upstream monitor legitimately backs one local row per tenant");
    }

    [Fact]
    public async Task MonitorRegistration_ScopesTheStoredRowToItsTenant()
    {
        var tenantId = Guid.NewGuid();
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnMonitorRegister(new IdentomatMonitorRegisterResponse { Result = "monitor-xyz" });
        var service = NewService(transport, store);

        await service.RegisterMonitorAsync(Guid.NewGuid(), tenantId, "John Doe", null);

        var saved = store.SavedMonitors.Should().ContainSingle().Subject;
        saved.TenantScope.Should().Be(tenantId.ToString("N"),
            "tenant_scope is the uniqueness domain; without it the UNIQUE index is global");
    }

    [Fact]
    public async Task MonitorRemoval_LeavesTheSharedUpstreamMonitorAloneWhileAnotherTenantWatches()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var store = new InMemoryKycScreeningStore();
        store.SeedMonitor("John Doe", string.Empty, "monitor-abc", tenantId: tenantA);
        store.SeedMonitor("John Doe", string.Empty, "monitor-abc", tenantId: tenantB);
        var transport = new FakeTransport();
        var service = NewService(transport, store);

        var removed = await service.RemoveMonitorAsync(tenantA, "monitor-abc");

        removed.IsError.Should().BeFalse(removed.Message);
        transport.CallCount.Should().Be(0,
            "unregistering the shared upstream monitor would silently stop tenant B's "
            + "sanctions monitoring");
        store.SavedMonitors.Single(monitor => monitor.TenantScope == tenantA.ToString("N"))
            .State.Should().Be(KycMonitorState.REMOVED);
        store.SavedMonitors.Single(monitor => monitor.TenantScope == tenantB.ToString("N"))
            .State.Should().Be(KycMonitorState.ACTIVE);
    }

    [Fact]
    public async Task Poll_FansOneUpstreamSearchOutAcrossEveryTenantSharingTheMonitor()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var store = new InMemoryKycScreeningStore();
        store.SeedMonitor("John Doe", string.Empty, "m1", tenantId: tenantA, subjectUserId: Guid.NewGuid());
        store.SeedMonitor("John Doe", string.Empty, "m1", tenantId: tenantB, subjectUserId: Guid.NewGuid());
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 1, ("m1", "2026-08-20T00:00:00Z")));
        transport.OnSearch(new IdentomatScreeningSearchResponse
        {
            Result = [new IdentomatScreeningHit { Score = 99, PersonId = "p1", Topics = ["Wanted person"] }],
        });
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.IsError.Should().BeFalse();
        transport.SearchCallCount.Should().Be(1,
            "one shared query shape costs ONE vendor call however many tenants watch it");
        store.SavedResults.Should().HaveCount(2,
            "each tenant needs its own result row, scoped to its own subject");
        store.SavedResults.Select(result => result.TenantId)
            .Should().BeEquivalentTo(new[] { tenantA.ToString("N"), tenantB.ToString("N") });
    }

    // ── Pagination: an unread page is not an absence of hits ────────────────

    [Fact]
    public async Task Screen_PagesUntilAShortRead_SoAHitPastTheFirstWindowIsStillSeen()
    {
        // The response carries no `total` and no documented sort order, so a single
        // unpaged window silently drops everything past it.
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnSearchPages(
            FullPage(pageSize: 3, score: 10),
            new IdentomatScreeningSearchResponse
            {
                Result = [new IdentomatScreeningHit { Score = 99, PersonId = "sanctioned", Topics = ["Sanctioned entity"] }],
            });
        var service = NewService(transport, store, searchPageSize: 3);

        await service.ScreenAsync(Guid.NewGuid(), null, "John Doe", null);

        transport.SearchCallCount.Should().Be(2, "a full page means there may be more");
        var saved = store.SavedResults.Should().ContainSingle().Subject;
        saved.HitCount.Should().Be(4);
        saved.Decision.Should().Be(KycScreeningDecision.HIT,
            "the sanctions hit sat at position 4 of a 3-wide window");
        saved.ResultsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Screen_WhenTheHitListSaturatesThePageBudget_IsNeverAPass()
    {
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnSearchPages(FullPage(pageSize: 2, score: 10), FullPage(pageSize: 2, score: 10));
        var service = NewService(transport, store, searchPageSize: 2, searchMaxPages: 2);

        await service.ScreenAsync(Guid.NewGuid(), null, "John Doe", null);

        var saved = store.SavedResults.Should().ContainSingle().Subject;
        saved.ResultsTruncated.Should().BeTrue();
        saved.Decision.Should().NotBe(KycScreeningDecision.NO_HIT,
            "an unread sanctions list is not a clean one");
    }

    [Fact]
    public async Task Screen_WhenALaterPageFails_FailsClosedInsteadOfKeepingThePrefix()
    {
        var store = new InMemoryKycScreeningStore();
        var transport = new FakeTransport();
        transport.OnSearchPages(FullPage(pageSize: 2, score: 10));
        var service = NewService(transport, store, searchPageSize: 2);

        var screened = await service.ScreenAsync(Guid.NewGuid(), null, "John Doe", null);

        screened.IsError.Should().BeTrue(
            "the hits already read may be the harmless prefix of a list whose adverse "
            + "entries were never fetched");
        store.SavedResults.Should().BeEmpty();
    }

    [Fact]
    public void RiskPolicy_TruncatedEmptyResultSet_IsManualReviewNotNoHit()
        => NewPolicy().Assess([], resultsTruncated: true)
            .Decision.Should().Be(KycScreeningDecision.MANUAL_REVIEW);

    // ── The high-water mark never advances past a record it could not classify ─

    [Fact]
    public async Task Poll_WhenAMonitorLookupFails_AbortsWithoutAdvancingTheHighWaterMark()
    {
        // Treating a STORE error like "not ours" and advancing the mark filters the
        // skipped record out of every future cycle: a sanctions change on it would
        // never be seen again.
        var store = new InMemoryKycScreeningStore
        {
            PollState = new KycScreeningPollState
            {
                Id = KycScreeningPollState.LocalId,
                HighWaterMarkDate = "2026-08-01T00:00:00Z",
            },
            FailMonitorLookup = true,
        };
        var transport = new FakeTransport();
        transport.OnMonitorList(Page(total: 1, ("m1", "2026-08-20T00:00:00Z")));
        var service = NewService(transport, store);

        var polled = await service.PollMonitoringAsync();

        polled.Result!.AbortedOnStoreError.Should().BeTrue();
        polled.Result.NextHighWaterMarkDate.Should().Be("2026-08-01T00:00:00Z",
            "the mark must stay where it was, or the unclassified record is lost forever");
        store.PollSuccessCount.Should().Be(0);
        store.PollFailureCount.Should().Be(1, "a failed cycle parks on backoff");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static KycScreeningRiskPolicy NewPolicy(int threshold = 80, int autoBlock = 95)
        => new(Options.Create(new KycSettings
        {
            Identomat = new IdentomatKycSettings
            {
                Screening = new IdentomatScreeningSettings
                {
                    ScoreThreshold = threshold,
                    AutoBlockScore = autoBlock,
                },
            },
        }));

    private static IdentomatScreeningService NewService(
        FakeTransport transport,
        InMemoryKycScreeningStore store,
        int scoreThreshold = 80,
        IKycTenantQuota? quota = null,
        int searchPageSize = 100,
        int searchMaxPages = 5)
    {
        var options = Options.Create(new KycSettings
        {
            Identomat = new IdentomatKycSettings
            {
                BeginBaseUrl = "https://external-api.identomat.com",
                CompanyKey = "company-key-value",
                Screening = new IdentomatScreeningSettings
                {
                    ScoreThreshold = scoreThreshold,
                    AutoBlockScore = 95,
                    MonitorPollPageSize = 100,
                    SearchPageSize = searchPageSize,
                    SearchMaxPages = searchMaxPages,
                },
            },
        });
        return new IdentomatScreeningService(
            options,
            transport,
            new KycScreeningRiskPolicy(options),
            store,
            quota ?? IdentomatTestFactory.AlwaysAllowQuota());
    }

    /// <summary>A page that exactly fills the window, so the caller must ask for more.</summary>
    private static IdentomatScreeningSearchResponse FullPage(int pageSize, int score)
        => new()
        {
            Result = Enumerable.Range(0, pageSize)
                .Select(index => new IdentomatScreeningHit
                {
                    Score = score,
                    PersonId = $"p{index}",
                    Topics = ["Close associate"],
                })
                .ToList(),
        };

    private static IdentomatMonitorListResponse Page(
        int total,
        params (string Id, string LastUpdated)[] records)
        => new()
        {
            Result = new IdentomatMonitorListPage
            {
                Start = 0,
                Limit = 100,
                Total = total,
                Data = records
                    .Select(record => new IdentomatMonitorListRecord
                    {
                        Id = record.Id,
                        FullName = record.Id,
                        LastUpdatedDate = record.LastUpdated,
                    })
                    .ToList(),
            },
        };

    /// <summary>Records every Identomat call so budget-shape assertions are possible.</summary>
    private sealed class FakeTransport : IIdentomatHttpTransport
    {
        private readonly Queue<IdentomatScreeningSearchResponse> _searchPages = new();
        private IdentomatScreeningSearchResponse? _search;
        private IdentomatMonitorRegisterResponse? _monitorRegister;
        private IdentomatMonitorListResponse? _monitorList;

        public int CallCount { get; private set; }
        public int SearchCallCount { get; private set; }
        public int MonitorListCallCount { get; private set; }
        public bool FailMonitorList { get; init; }
        public IdentomatScreeningSearchRequest? LastSearchRequest { get; private set; }
        public IdentomatMonitorRegisterRequest? LastMonitorRegisterRequest { get; private set; }

        public void OnSearch(IdentomatScreeningSearchResponse response) => _search = response;

        /// <summary>Answers successive search pages in order; an exhausted queue fails.</summary>
        public void OnSearchPages(params IdentomatScreeningSearchResponse[] pages)
        {
            foreach (var page in pages)
                _searchPages.Enqueue(page);
        }
        public void OnMonitorRegister(IdentomatMonitorRegisterResponse response) => _monitorRegister = response;
        public void OnMonitorList(IdentomatMonitorListResponse response) => _monitorList = response;

        public Task<AZOAResult<TResponse>> PostAsync<TRequest, TResponse>(
            string baseUrl,
            string path,
            TRequest body,
            string operation,
            CancellationToken ct = default)
            where TResponse : class
        {
            CallCount++;
            switch (operation)
            {
                case "screening-search":
                    SearchCallCount++;
                    LastSearchRequest = body as IdentomatScreeningSearchRequest;
                    if (_searchPages.Count > 0)
                        return Result<TResponse>(_searchPages.Dequeue());
                    return Result<TResponse>(_search);
                case "monitor-register":
                    LastMonitorRegisterRequest = body as IdentomatMonitorRegisterRequest;
                    return Result<TResponse>(_monitorRegister);
                case "monitor-list":
                    MonitorListCallCount++;
                    return FailMonitorList
                        ? Task.FromResult(AZOAResult<TResponse>.FailureWithCode(
                            "budget exhausted", AzoaErrorCodes.DependencyUnavailable))
                        : Result<TResponse>(_monitorList);
                default:
                    return Task.FromResult(AZOAResult<TResponse>.Failure($"unstubbed: {operation}"));
            }
        }

        private static Task<AZOAResult<TResponse>> Result<TResponse>(object? configured)
            where TResponse : class
            => Task.FromResult(configured is TResponse typed
                ? AZOAResult<TResponse>.Success(typed)
                : AZOAResult<TResponse>.Failure("unstubbed response"));
    }

}
