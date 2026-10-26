// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Tests.Allocation;

/// <summary>
/// The on-ramp issuance shape (no acting tenant, NodeOperator draw) re-gates the payer's KYC
/// in the scope the payer was actually approved in. See Managers/AGENTS.md §identity scope.
/// </summary>
public class IssueUnitsIdentityScopeTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    public static TheoryData<bool, bool, bool> Cases => new()
    {
        // approvedInTenantScope, namesIdentityTenant, issues
        { true,  true,  true  }, // tenant-custodial payer, rail names the tenant: issues
        { true,  false, false }, // the live bug: re-gated globally, where no approval exists
        { false, true,  false }, // a genuinely unverified payer still refuses
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnRampIssuance_GatesThePayerInItsOwnTenantScope(
        bool approvedInTenantScope, bool namesIdentityTenant, bool issues)
    {
        var harness = new AllocationTestHarness();
        harness.PreparerReturns(alreadyPrepared: true);
        var gate = new ScopeStrictGate();
        if (approvedInTenantScope) gate.Approve(harness.UserId, Tenant);
        var valueAccess = new ValueAccessService(gate);
        harness.ValueAccess
            .Setup(access => access.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid participant, Guid? scope, CancellationToken ct) => valueAccess.RequireValueAccessAsync(participant, scope, ct));
        var manager = harness.Build();

        var result = await manager.AllocateAsync(
            harness.UserId, AllocationTestHarness.IssueUnitsRequest(),
            harness.UserId, "onramp-issue", AllocationTestHarness.ApiKeyId,
            actingTenantId: null,
            drawAuthority: ReserveDrawAuthority.NodeOperator,
            identityTenantId: namesIdentityTenant ? Tenant : null);

        result.IsError.Should().Be(!issues, result.Message);
        if (issues)
            harness.Steps.Should().Contain("broadcast");
        else
        {
            result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
            harness.Steps.Should().BeEmpty("a refused payer moves nothing");
        }
    }

    /// <summary>Mirrors KycGateService: an approval is found only in the exact scope it was granted in.</summary>
    private sealed class ScopeStrictGate : IRealValueKycGate
    {
        private readonly HashSet<(Guid User, Guid? Scope)> _approved = new();

        public void Approve(Guid user, Guid? scope) => _approved.Add((user, scope));

        public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Decide(userId, null));

        public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(Decide(userId, tenantId));

        private AZOAResult<bool> Decide(Guid user, Guid? scope)
            => _approved.Contains((user, scope))
                ? AZOAResult<bool>.Success(true)
                : AZOAResult<bool>.Failure(KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage);
    }
}
