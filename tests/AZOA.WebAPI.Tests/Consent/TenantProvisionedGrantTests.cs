// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

/// <summary>
/// W3.6 — the implied, revocable consent grant a tenant-provisioned user carries.
///
/// <para>The property under test is not "a grant is created" but "authority is created
/// exactly once, is never outbound, and can be taken back for good". Each test names the
/// way that could fail: a value scope smuggled into a no-UX grant, a re-provision that
/// resurrects a revoked grant, a claimed user re-attached to their old tenant, or an
/// issuer that quietly does nothing while the provisioning call reports success.</para>
/// </summary>
public class TenantProvisionedGrantTests
{
    private readonly Mock<IConsentManager> _consent = new();
    private readonly Mock<IConsentGrantStore> _grants = new();
    private readonly TenantProvisionedGrantIssuer _issuer;

    public TenantProvisionedGrantTests()
    {
        NoPriorGrant();
        _consent.Setup(c => c.GrantParticipationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid grantor, Guid tenant, string reference, IEnumerable<string> scopes, CancellationToken _) =>
                new AZOAResult<ConsentGrant>
                {
                    Result = new ConsentGrant
                    {
                        GrantorUserId = grantor,
                        TenantId = tenant,
                        ParticipationRef = reference,
                        Origin = GrantOrigin.Participation,
                        Scopes = scopes.ToList(),
                    },
                });

        _issuer = new TenantProvisionedGrantIssuer(
            _consent.Object, _grants.Object, NullLogger<TenantProvisionedGrantIssuer>.Instance);
    }

    // ── The scope ceiling (H4) ────────────────────────────────────────────────

    [Fact]
    public void ImpliedScopes_CarryNoOutboundValueSigningScope()
    {
        TenantProvisionedGrant.CarriesNoValueSigningScope.Should().BeTrue();
        TenantProvisionedGrant.ImpliedScopes.Should().NotIntersectWith(AzoaScopes.ValueSigningScopes);
    }

    [Fact]
    public void ImpliedScopes_CoverTheInboundAllocationScopeOnly()
    {
        // nft:mint is the scope AllocationManager stamps on an inbound Mint — it credits the
        // person's account and spends nothing of theirs. transfer:sign, which moves value
        // out, is deliberately absent and needs a deliberate UserExplicit grant.
        TenantProvisionedGrant.ImpliedScopes.Should().Contain(AzoaScopes.NftMint);
        TenantProvisionedGrant.ImpliedScopes.Should().NotContain(AzoaScopes.TransferSign);
    }

    // ── The participation reference ───────────────────────────────────────────

    [Fact]
    public void ParticipationRef_IsDeterministicPerTenantAndExternalUser()
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        var reference = TenantProvisionedGrant.ParticipationRefFor(tenant, "user-42");

        reference.Should().StartWith(TenantProvisionedGrant.ParticipationRefPrefix);
        reference.Should().Be(TenantProvisionedGrant.ParticipationRefFor(tenant, " user-42 "));
        reference.Should().NotBe(TenantProvisionedGrant.ParticipationRefFor(otherTenant, "user-42"));
        reference.Should().NotBe(TenantProvisionedGrant.ParticipationRefFor(tenant, "user-43"));
        reference.Should().NotContain("user-42");
    }

    // ── Issuance ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_CreatesAParticipationGrantScopedToTheProvisioningTenant()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        var result = await _issuer.IssueOnProvisionAsync(tenant, user, "user-42");

        result.IsError.Should().BeFalse();
        _consent.Verify(c => c.GrantParticipationAsync(
            user,
            tenant,
            TenantProvisionedGrant.ParticipationRefFor(tenant, "user-42"),
            It.Is<IEnumerable<string>>(s => s.SequenceEqual(TenantProvisionedGrant.ImpliedScopes)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Issue_IsIdempotentOverAnExistingLiveGrant()
    {
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        GivenPriorGrant(tenant, user, "user-42", revokedAt: null);

        var result = await _issuer.IssueOnProvisionAsync(tenant, user, "user-42");

        result.IsError.Should().BeFalse();
        NoGrantWasWritten();
    }

    [Fact]
    public async Task Issue_NeverResurrectsAGrantThePersonRevoked()
    {
        // The whole point of "revocable": a tenant must not be able to re-provision its way
        // back to authority a person withdrew.
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        GivenPriorGrant(tenant, user, "user-42", revokedAt: DateTime.UtcNow.AddMinutes(-5));

        var result = await _issuer.IssueOnProvisionAsync(tenant, user, "user-42");

        result.IsError.Should().BeFalse();
        result.Result!.RevokedAt.Should().NotBeNull();
        result.Message.Should().Contain("revoked");
        NoGrantWasWritten();
    }

    [Fact]
    public async Task Issue_FailsClosedWhenTheGrantHistoryCannotBeRead()
    {
        _grants.Setup(g => g.ListByTenantAndParticipationRefAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>> { IsError = true, Message = "store down" });

        var result = await _issuer.IssueOnProvisionAsync(Guid.NewGuid(), Guid.NewGuid(), "user-42");

        result.IsError.Should().BeTrue();
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        NoGrantWasWritten();
    }

    // ── ProvisionChildAsync wiring ────────────────────────────────────────────

    [Fact]
    public async Task ProvisionChild_IssuesTheImpliedGrantOnFirstCreate()
    {
        var tenant = Guid.NewGuid();
        var store = UserStoreWithNoExistingChild();
        var manager = NewTenantManager(store, _issuer);

        var result = await manager.ProvisionChildAsync(tenant, new ProvisionChildModel { ExternalUserId = "user-42" });

        result.IsError.Should().BeFalse();
        result.Message.Should().NotContain(TenantManager.NoImpliedGrantNotice);
        _consent.Verify(c => c.GrantParticipationAsync(
            result.Result!.UserId,
            tenant,
            TenantProvisionedGrant.ParticipationRefFor(tenant, "user-42"),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProvisionChild_OnAClaimedUser_IssuesNothing()
    {
        // A claimed user is self-sovereign; claiming already revoked every grant they made.
        // Re-provisioning them must not silently hand the old tenant a fresh one.
        var tenant = Guid.NewGuid();
        var store = new Mock<IUserStore>();
        // Claiming severs OwnerTenantId, so the tenant-scoped lookup misses and the manager
        // resolves the original account by its deterministic, tenant-partitioned identity.
        store.Setup(s => s.GetByTenantAndExternalUserAsync(tenant, "user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = null });
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new AZOAResult<IUser>
            {
                Result = new User
                {
                    Id = id,
                    OwnerTenantId = null,
                    ExternalUserId = "user-42",
                    Username = "claimed",
                    Email = "claimed@example.test",
                },
            });
        var manager = NewTenantManager(store, _issuer);

        var result = await manager.ProvisionChildAsync(tenant, new ProvisionChildModel { ExternalUserId = "user-42" });

        result.IsError.Should().BeFalse();
        result.Message.Should().Contain("claimed");
        NoGrantWasWritten();
    }

    [Fact]
    public async Task ProvisionChild_WithNoIssuerWired_SaysNoAuthorityWasGranted()
    {
        // The degraded mode is declared, not accidental: absence of the issuer can only fail
        // to grant authority, never confer it, and the response refuses to read as clean.
        var tenant = Guid.NewGuid();
        var manager = NewTenantManager(UserStoreWithNoExistingChild(), provisionedGrants: null);

        var result = await manager.ProvisionChildAsync(tenant, new ProvisionChildModel { ExternalUserId = "user-42" });

        result.IsError.Should().BeFalse();
        result.Message.Should().Contain(TenantManager.NoImpliedGrantNotice);
        NoGrantWasWritten();
    }

    [Fact]
    public async Task ProvisionChild_WhenTheGrantCannotBeIssued_FailsTheProvisioning()
    {
        // A user the tenant can never act for is a half-built onboarding, not a success.
        // Provisioning is idempotent, so the caller retries and the issuer heals it once.
        _consent.Setup(c => c.GrantParticipationAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ConsentGrant> { IsError = true, Message = "grant store down" });
        var manager = NewTenantManager(UserStoreWithNoExistingChild(), _issuer);

        var result = await manager.ProvisionChildAsync(
            Guid.NewGuid(), new ProvisionChildModel { ExternalUserId = "user-42" });

        result.IsError.Should().BeTrue();
        result.Result.Should().BeNull();
        result.Message.Should().StartWith("TENANT_CONSENT_UNAVAILABLE:");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void NoPriorGrant()
        => _grants.Setup(g => g.ListByTenantAndParticipationRefAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>> { Result = Array.Empty<ConsentGrant>() });

    private void GivenPriorGrant(Guid tenant, Guid user, string externalUserId, DateTime? revokedAt)
        => _grants.Setup(g => g.ListByTenantAndParticipationRefAsync(
                tenant,
                TenantProvisionedGrant.ParticipationRefFor(tenant, externalUserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ConsentGrant>>
            {
                Result = new[]
                {
                    new ConsentGrant
                    {
                        GrantorUserId = user,
                        TenantId = tenant,
                        Origin = GrantOrigin.Participation,
                        ParticipationRef = TenantProvisionedGrant.ParticipationRefFor(tenant, externalUserId),
                        Scopes = TenantProvisionedGrant.ImpliedScopes.ToList(),
                        RevokedAt = revokedAt,
                    },
                },
            });

    private void NoGrantWasWritten()
        => _consent.Verify(c => c.GrantParticipationAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);

    private static Mock<IUserStore> UserStoreWithNoExistingChild()
    {
        var store = new Mock<IUserStore>();
        store.Setup(s => s.GetByTenantAndExternalUserAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { Result = null });
        store.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IUser> { IsError = true, Message = "User not found." });
        store.Setup(s => s.CreateIfAbsentAsync(It.IsAny<IUser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IUser user, CancellationToken _) => new AZOAResult<IUser> { Result = user });
        return store;
    }

    private TenantManager NewTenantManager(
        Mock<IUserStore> userStore, ITenantProvisionedGrantIssuer? provisionedGrants)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "super-secret-key-for-testing-only-quite-long-enough!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test",
            })
            .Build();

        return new TenantManager(userStore.Object, config, _grants.Object, provisionedGrants);
    }
}
