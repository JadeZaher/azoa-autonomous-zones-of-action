using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class KycControlPlaneAuditPagingTests
{
    [Fact]
    public async Task ApprovedQueue_ProfileTrustRevisionChanged_ProjectsSubmissionAsStale()
    {
        var tenantId = Guid.NewGuid();
        var submission = new KycSubmission
        {
            Id = Guid.NewGuid().ToString("N"),
            AvatarId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId.ToString("N"),
            Provider = KycProvider.MANUAL,
            ProviderKey = "manual",
            ProviderSelectionVersion = 3,
            ProviderTrustRevision = 1,
            Status = KycStatus.APPROVED,
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            CreatedDate = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var kycStore = new Mock<IKycStore>();
        kycStore.Setup(store => store.GetEffectiveOperatorPageAsync(
                "approved", 0, 11, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IReadOnlyList<KycSubmission>>.Success([submission]));
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(candidate => candidate.ProviderKey).Returns("manual");
        var registry = new Mock<IKycProviderRegistry>();
        registry.Setup(candidate => candidate.ResolveTenantAsync(
                tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
                provider.Object,
                new KycSettings(),
                tenantId,
                SelectionVersion: 3,
                TrustRevision: 2,
                DisplayName: "Manual")));
        var manager = new KycControlPlaneManager(
            Mock.Of<IKycControlStore>(),
            kycStore.Object,
            registry.Object,
            Mock.Of<IKycManager>(),
            Mock.Of<IApiKeyStore>(),
            Mock.Of<IAvatarStore>(),
            Mock.Of<IAdminBootstrapStateStore>(),
            Mock.Of<IHostEnvironment>(),
            new ConfigurationBuilder().Build());

        var result = await manager.ListQueueAsync("approved", 10, null);

        result.IsError.Should().BeFalse(result.Message);
        result.Result!.Items.Should().ContainSingle();
        result.Result.Items[0].Status.Should().Be("STALE");
        result.Result.Items[0].HumanReviewAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task AuditCursor_AnchorsPageWhenNewerRowIsInsertedBetweenRequests()
    {
        var actorId = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-07-18T12:00:00Z");
        var original = Enumerable.Range(1, 4)
            .Select(index => Audit(start.AddMinutes(index), actorId, index))
            .OrderByDescending(row => row.OccurredAt)
            .ThenByDescending(row => row.Id, StringComparer.Ordinal)
            .ToList();
        var rows = original.ToList();
        var observed = new List<KycControlAuditCursor?>();
        var store = new Mock<IKycControlStore>();
        store.Setup(value => value.ListAuditPageAsync(
                It.IsAny<int>(),
                It.IsAny<KycControlAuditCursor?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                int limit,
                KycControlAuditCursor? before,
                Guid? _tenantId,
                string? _providerKey,
                string? _action,
                CancellationToken _ct) =>
            {
                observed.Add(before);
                var page = rows
                    .Where(row => before is null
                        || row.OccurredAt < before.OccurredAt
                        || (row.OccurredAt == before.OccurredAt
                            && string.CompareOrdinal(row.Id, before.RecordId) < 0))
                    .OrderByDescending(row => row.OccurredAt)
                    .ThenByDescending(row => row.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .ToList();
                return Task.FromResult(AZOAResult<IReadOnlyList<KycControlAudit>>.Success(page));
            });
        var manager = Build(store.Object);

        var first = await manager.ListAuditAsync(2, null, null, null, null);
        rows.Add(Audit(start.AddMinutes(5), actorId, 5));
        var second = await manager.ListAuditAsync(
            2,
            first.Result!.NextCursor,
            null,
            null,
            null);

        first.IsError.Should().BeFalse(first.Message);
        second.IsError.Should().BeFalse(second.Message);
        first.Result.NextCursor.Should().MatchRegex("^[A-Za-z0-9_-]+$")
            .And.NotContain("=");
        observed.Should().HaveCount(2);
        observed[0].Should().BeNull();
        observed[1].Should().Be(new KycControlAuditCursor(
            original[1].OccurredAt,
            original[1].Id));

        first.Result.Items.Concat(second.Result!.Items)
            .Select(item => item.Id)
            .Should().Equal(original.Select(item => Guid.Parse(item.Id)));
        second.Result.Items.Select(item => item.Id)
            .Should().NotContain(Guid.Parse(rows.Single(item => item.Version == 5).Id));
    }

    private static KycControlAudit Audit(DateTimeOffset occurredAt, Guid actorId, long version)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Action = "profile.metadata-change",
            ProviderKey = "manual",
            Version = version,
            ActorAvatarId = actorId.ToString("N"),
            OccurredAt = occurredAt,
        };

    private static KycControlPlaneManager Build(IKycControlStore store)
        => new(
            store,
            Mock.Of<IKycStore>(),
            Mock.Of<IKycProviderRegistry>(),
            Mock.Of<IKycManager>(),
            Mock.Of<IApiKeyStore>(),
            Mock.Of<IAvatarStore>(),
            Mock.Of<IAdminBootstrapStateStore>(),
            Mock.Of<IHostEnvironment>(),
            new ConfigurationBuilder().Build());
}
