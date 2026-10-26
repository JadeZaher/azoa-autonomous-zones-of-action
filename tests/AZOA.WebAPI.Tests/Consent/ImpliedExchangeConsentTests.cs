// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Consent;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Consent;

/// <summary>
/// The W3.6 implied grant covers the W5.1 reserve exchange (Core/AGENTS.md §consent-scope-coverage):
/// a tenant-driven sponsorship of a custodial child passes the exchange edge probe through the REAL
/// <see cref="TenantConsentGate"/> over a store with the production live-filter + Covers semantics.
/// </summary>
public class ImpliedExchangeConsentTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _childId = Guid.NewGuid();
    private readonly InMemoryGrantStore _grants = new();
    private readonly Mock<IReserveExchangeManager> _exchange = new();

    public ImpliedExchangeConsentTests()
        => _exchange.Setup(m => m.ExchangeAsync(It.IsAny<ExchangeRequest>(), It.IsAny<ExchangeCallerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ExchangeReceipt>.Success(new ExchangeReceipt()));

    [Fact]
    public async Task A_custodial_child_holding_only_the_implied_grant_reaches_the_exchange()
    {
        // The exact stored shape of a pre-existing provisioned grant: swap:sign is NOT in the row.
        GivenImpliedGrant();

        var response = await Controller().Exchange(_childId, new ExchangeRequest { PreviewOnly = true }, "idem-1", CancellationToken.None);

        response.Result.Should().BeOfType<OkObjectResult>();
        ExchangeRan(Times.Once());
    }

    [Fact]
    public async Task After_the_child_revokes_the_implied_grant_the_exchange_is_refused()
    {
        GivenImpliedGrant(revokedAt: DateTime.UtcNow.AddSeconds(-1));

        var response = await Controller().Exchange(_childId, new ExchangeRequest { PreviewOnly = true }, "idem-2", CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ExchangeRan(Times.Never());
    }

    [Fact]
    public async Task A_first_party_user_with_no_grant_to_the_tenant_is_refused()
    {
        var response = await Controller().Exchange(_childId, new ExchangeRequest { PreviewOnly = true }, "idem-3", CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ExchangeRan(Times.Never());
    }

    [Fact]
    public async Task A_participation_grant_that_is_not_tenant_provisioned_does_not_cover_the_exchange()
    {
        // A join-flow participation grant has the same stored scopes but no reserved prefix.
        GivenImpliedGrant(participationRef: "ardanova:project-member:42");

        var response = await Controller().Exchange(_childId, new ExchangeRequest { PreviewOnly = true }, "idem-4", CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ExchangeRan(Times.Never());
    }

    [Theory]
    [InlineData(AzoaScopes.TransferSign, false)]
    [InlineData(AzoaScopes.GrantSign, false)]
    [InlineData(AzoaScopes.NftBurn, false)]
    [InlineData(AzoaScopes.SwapSign, true)]
    [InlineData(AzoaScopes.UnitsIssue, true)]
    public async Task Under_the_same_implied_grant_only_the_exchange_and_stored_scopes_are_consented(string scope, bool allowed)
    {
        GivenImpliedGrant();
        var probe = new SigningContext(_childId, Guid.Empty, IsPlatform: false).ActingAs(_tenantId, scope, _childId);

        var decision = await Gate().EnsureAllowedAsync(probe);

        decision.IsError.Should().Be(!allowed);
    }

    [Fact]
    public void The_exchange_fence_locks_an_implied_grant_witness_and_refuses_a_forged_reference()
    {
        var implied = Authority(TenantProvisionedGrant.ParticipationRefFor(_tenantId, "demo-sponsor"));
        var forged = Authority("ardanova:project-member:42");
        var explicitWithoutScope = Authority(participationRef: null);

        ExchangeAuthorityFence.Create(implied, "exchange_test").Should().NotBeNull();
        ExchangeAuthorityFence.Create(forged, "exchange_test").Should().BeNull();
        ExchangeAuthorityFence.Create(explicitWithoutScope, "exchange_test").Should().BeNull();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private const string StoredImpliedScopes = "action:execute,kyc:read,kyc:submit,nft:mint,units:issue,wallet:manage";

    private void GivenImpliedGrant(DateTime? revokedAt = null, string? participationRef = null)
        => _grants.Rows.Add(new ConsentGrant
        {
            GrantorUserId = _childId,
            TenantId = _tenantId,
            Origin = GrantOrigin.Participation,
            ParticipationRef = participationRef ?? TenantProvisionedGrant.ParticipationRefFor(_tenantId, "demo-sponsor"),
            Scopes = StoredImpliedScopes.Split(',').ToList(),
            RevokedAt = revokedAt,
        });

    private ExchangeParticipantAuthority Authority(string? participationRef)
    {
        var credential = new SigningCredentialWitness(1, "ApiKey", _tenantId, null, null, null, null, Guid.NewGuid(),
            new string('a', 64), [AzoaScopes.SwapSign], false, [AzoaScopes.SwapSign]);
        return new(1, new(1, credential, [AzoaScopes.SwapSign], []), _childId, null,
            new(Guid.NewGuid(), _childId, "child-address", "Algorand"),
            new(Guid.NewGuid(), AzoaScopes.SwapSign, StoredImpliedScopes, DateTimeOffset.UtcNow, null, participationRef));
    }

    private TenantConsentGate Gate()
        => new(_grants, new NullAudit(), new AZOA.WebAPI.Tests.TestSupport.ApprovedKybGate(), NullLogger<TenantConsentGate>.Instance);

    private void ExchangeRan(Times times)
        => _exchange.Verify(m => m.ExchangeAsync(It.IsAny<ExchangeRequest>(), It.IsAny<ExchangeCallerContext>(), It.IsAny<CancellationToken>()), times);

    private ExchangeController Controller()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, _tenantId.ToString()),
            new("UserId", _tenantId.ToString()),
            new("AuthMethod", "ApiKey"),
            new("ApiKeyId", Guid.NewGuid().ToString()),
            new("scope", ReserveExchangeManager.ExchangeSigningScope),
        };
        return new ExchangeController(_exchange.Object, Gate())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            },
        };
    }

    /// <summary>Mirrors SurrealConsentGrantStore: server-side liveness filter, then Covers owns the scope word.</summary>
    private sealed class InMemoryGrantStore : IConsentGrantStore
    {
        public List<ConsentGrant> Rows { get; } = new();

        public Task<AZOAResult<ConsentGrant>> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<ConsentGrant> { Result = Rows.FirstOrDefault(r => r.Id == id) });

        public Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByGrantorAsync(Guid grantorUserId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IEnumerable<ConsentGrant>> { Result = Rows.Where(r => r.GrantorUserId == grantorUserId).ToList() });

        public Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IEnumerable<ConsentGrant>> { Result = Rows.Where(r => r.TenantId == tenantId).ToList() });

        public Task<AZOAResult<ConsentGrant>> FindCoveringGrantAsync(Guid grantorUserId, Guid tenantId, string scope, DateTime now, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<ConsentGrant>
            {
                Result = Rows.Where(r => r.GrantorUserId == grantorUserId && r.TenantId == tenantId && r.IsLiveAt(now))
                    .FirstOrDefault(r => r.Covers(scope, now)),
            });
    }

    private sealed class NullAudit : IConsentAuditStore
    {
        public Task<AZOAResult<bool>> AppendAsync(ConsentAuditEntry entry, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<bool> { Result = true });

        public Task<AZOAResult<IEnumerable<ConsentAuditEntry>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(new AZOAResult<IEnumerable<ConsentAuditEntry>> { Result = [] });
    }
}
