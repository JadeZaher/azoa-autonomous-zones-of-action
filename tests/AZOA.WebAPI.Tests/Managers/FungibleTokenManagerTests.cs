using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Managers;

public sealed class FungibleTokenManagerTests
{
    [Theory]
    [InlineData(ReserveMintTargets.PlatformReserve, "platform-reserve")]
    [InlineData(ReserveMintTargets.CallerWallet, "caller-wallet")]
    public async Task Admission_freezes_creator_roles_without_signing_or_registering_unconfirmed_assets(string target, string creator)
    {
        var harness = new FungibleAdmissionHarness(target);
        var result = await harness.AdmitAsync();
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.AssetId.Should().BeEmpty();
        result.Result.Operation!.State.Should().Be("AwaitingConsent");
        var command = harness.Commands.Values.Single();
        command.Intent.CreatorAddress.Should().Be(creator);
        command.Intent.ManagerAddress.Should().Be(creator);
        command.Intent.ReserveAddress.Should().Be(creator);
        command.Intent.FreezeAddress.Should().Be(creator);
        command.Intent.ClawbackAddress.Should().Be(creator);
        command.Intent.Quote.Should().BeNull();
        command.SigningAuthority!.RequiredScopes.Should().Contain(AzoaScopes.TokenCreateSign);
        harness.Module.VerifyNoOtherCalls();
        harness.Reserve.Verify(service => service.RegisterAssetAsync(It.IsAny<ReserveAssetRegistration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_wallet_bootstraps_one_owned_wallet_but_still_only_admits_creation()
    {
        var harness = new FungibleAdmissionHarness();
        harness.WalletStore.Setup(store => store.GetByUserAsync(harness.Actor, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success([]));
        var result = await harness.AdmitAsync();
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.WalletProvisioned.Should().BeTrue();
        result.Result.WalletId.Should().Be(harness.Wallet.Id);
        result.Result.AssetId.Should().BeEmpty();
        harness.Module.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Original_key_replays_before_mutable_KYC_and_different_intent_conflicts()
    {
        var harness = new FungibleAdmissionHarness();
        var first = await harness.AdmitAsync();
        harness.ValueAccess.Setup(service => service.RequireValueAccessAsync(harness.Actor, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("Access revoked"));
        var replay = await harness.AdmitAsync();
        replay.IsError.Should().BeFalse();
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Operation!.OperationId.Should().Be(first.Result!.Operation!.OperationId);
        (await harness.AdmitAsync(harness.Request(1001))).IsError.Should().BeTrue();
        harness.Commands.Should().ContainSingle();
        harness.Module.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_actual_authority_and_cross_actor_requests_never_admit()
    {
        var harness = new FungibleAdmissionHarness();
        var request = harness.Request(); request.AuthenticationWitness = null;
        (await harness.AdmitAsync(request)).IsError.Should().BeTrue();
        (await harness.Manager.CreateAsync(harness.Actor, harness.Request(), Guid.NewGuid(), "key", "unused")).IsError.Should().BeTrue();
        harness.Commands.Should().BeEmpty();
        harness.Module.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reserve_owner_is_only_the_callers_own_tenant_and_binds_into_the_request_key()
    {
        var harness = new FungibleAdmissionHarness();
        var foreign = harness.Request(); foreign.ReserveOwnerTenantId = Guid.NewGuid();
        (await harness.AdmitAsync(foreign, "foreign")).IsError.Should().BeTrue();
        harness.Commands.Should().BeEmpty();

        await harness.AdmitAsync(key: "shared");                       // first-party admission: the node's own unit
        var owned = harness.Request(); owned.ReserveOwnerTenantId = harness.Actor;
        (await harness.AdmitAsync(owned, "shared")).IsError.Should().BeTrue("a key admitted as the platform unit cannot replay as tenant-owned");
        harness.Commands.Values.Single().ReserveTenantId().Should().BeNull();
    }

    [Fact]
    public async Task Policy_refusal_precedes_readiness_and_custody_and_keeps_consumer_code()
    {
        var harness = new FungibleAdmissionHarness(governance: new NodeGovernanceGuard(Options.Create(new NodeGovernanceOptions { AllowedAssetTypes = ["NFT"] })));
        var result = await harness.AdmitAsync();
        result.Code.Should().Be(ConsumerSafeErrorCodes.NotPermitted);
        ConsumerSafeErrors.LeaksRailDetail(result.Message).Should().BeFalse();
        harness.ValueAccess.Verify(service => service.RequireValueAccessAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.WalletStore.Verify(store => store.GetByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_readiness_receives_actual_tenant_and_refuses_before_wallet_admission()
    {
        var harness = new FungibleAdmissionHarness(); var tenant = Guid.NewGuid();
        harness.ValueAccess.Setup(service => service.RequireValueAccessAsync(harness.Actor, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Failure("Participant is not ready"));
        (await harness.Manager.CreateAsync(harness.Actor, harness.Request(tenant: tenant), harness.Actor, "original", "unused", tenant)).IsError.Should().BeTrue();
        harness.ValueAccess.Verify(service => service.RequireValueAccessAsync(harness.Actor, tenant, It.IsAny<CancellationToken>()), Times.Once);
        harness.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_reserve_or_wallet_infrastructure_failure_cannot_report_issuance()
    {
        var harness = new FungibleAdmissionHarness();
        harness.Reserve.Setup(service => service.GetAccountAsync("Algorand", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ReserveAccount>.Failure("Reserve unavailable"));
        (await harness.AdmitAsync()).IsError.Should().BeTrue();
        harness.WalletStore.Setup(store => store.GetByUserAsync(harness.Actor, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Storage offline"));
        await FluentActions.Awaiting(() => harness.AdmitAsync()).Should().ThrowAsync<IOException>();
        harness.Commands.Should().BeEmpty();
        harness.Module.VerifyNoOtherCalls();
    }
}
