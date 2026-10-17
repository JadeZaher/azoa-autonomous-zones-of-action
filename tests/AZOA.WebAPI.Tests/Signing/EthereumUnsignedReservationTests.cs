using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class EthereumUnsignedReservationTests
{
    [Fact]
    public async Task Unsigned_domain_reservation_never_signs_and_shares_nonce_ownership_with_existing_wrapper()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid();
        var reserved = await Reserve(fixture.Provider, fixture.Sender, actor, wallet, "swap:reservation");
        reserved.IsError.Should().BeFalse(reserved.Message);
        reserved.Result!.State.Should().Be("Reserved"); reserved.Result.SignedTransactionBase64.Should().BeNull();
        fixture.SigningCalls.Should().Be(0); fixture.Rpc.Wires.Should().BeEmpty();
        using var metadata = JsonDocument.Parse(reserved.Result.IntentJson);
        metadata.RootElement.GetProperty("Context").ValueKind.Should().Be(JsonValueKind.Null);
        metadata.RootElement.GetProperty("Owner").GetProperty("ActorUserId").GetGuid().Should().Be(actor);
        var replay = await Reserve(fixture.NewProvider(), fixture.Sender, actor, wallet, "swap:reservation");
        replay.Result!.Nonce.Should().Be(reserved.Result.Nonce);
        var existing = await fixture.Provider.PrepareValueTransferAsync(fixture.Intent("value:after-reservation"), default);
        existing.IsError.Should().BeFalse(existing.Message);
        fixture.Store.Snapshot.Select(row => row.Nonce).Distinct().Should().HaveCount(2);
        await fixture.Provider.ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty("neither an unsigned reservation nor a prepared outer operation grants egress");
    }

    [Fact]
    public async Task Wrong_wallet_or_canceled_reservation_refuses_before_custody()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid();
        var reserved = (await Reserve(fixture.Provider, fixture.Sender, actor, wallet, "swap:cancel")).Result!;
        var context = SigningContext.ForUser(actor, wallet).ForChain("Ethereum") with { Scope = AzoaScopes.SwapSign };
        var wrong = await Sign(fixture.Provider, reserved, context with { WalletId = Guid.NewGuid() });
        wrong.IsError.Should().BeTrue(); fixture.SigningCalls.Should().Be(0);
        var row = (await fixture.Store.GetAsync(reserved.Id))!; row.State = "Canceled";
        (await fixture.Store.UpdateAsync(row, row.Version)).Should().NotBeNull();
        var canceled = await Sign(fixture.Provider, reserved, context);
        canceled.IsError.Should().BeTrue(); fixture.SigningCalls.Should().Be(0);
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Canceling")]
    [InlineData("Canceled")]
    [InlineData("Reverted")]
    public async Task Saved_signed_candidate_cannot_be_returned_as_ready_after_cancel_or_revert(string state)
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid();
        var reserved = (await Reserve(fixture.Provider, fixture.Sender, actor, wallet, "swap:signed-cancel")).Result!;
        var context = SigningContext.ForUser(actor, wallet).ForChain("Ethereum") with { Scope = AzoaScopes.SwapSign };
        var signed = await Sign(fixture.Provider, reserved, context);
        signed.IsError.Should().BeFalse(signed.Message); fixture.SigningCalls.Should().Be(1);
        var row = (await fixture.Store.GetAsync(reserved.Id))!; row.State = state;
        (await fixture.Store.UpdateAsync(row, row.Version)).Should().NotBeNull();
        var replay = await Sign(fixture.Provider, reserved, context);
        replay.IsError.Should().BeTrue(); fixture.SigningCalls.Should().Be(1);
        await fixture.Provider.ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_winning_during_custody_cannot_publish_the_losing_signed_candidate()
    {
        using var fixture = new EthereumPreparedRecoveryTests.Fixture();
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid();
        var reserved = (await Reserve(fixture.Provider, fixture.Sender, actor, wallet, "swap:delayed-custody")).Result!;
        fixture.BeforeUserSigning = async () =>
        {
            var row = (await fixture.Store.GetAsync(reserved.Id))!; row.State = "Canceling";
            (await fixture.Store.UpdateAsync(row, row.Version)).Should().NotBeNull();
        };
        var context = SigningContext.ForUser(actor, wallet).ForChain("Ethereum") with { Scope = AzoaScopes.SwapSign };
        var signed = await Sign(fixture.Provider, reserved, context);
        signed.IsError.Should().BeTrue();
        var current = (await fixture.Store.GetAsync(reserved.Id))!;
        current.State.Should().Be("Canceling"); current.SignedTransactionBase64.Should().BeNull();
        await fixture.Provider.ReplayAuthorizedAsync(default);
        fixture.Rpc.Wires.Should().BeEmpty();
    }

    private static Task<AZOAResult<EthereumPreparation>> Reserve(EthereumProvider provider, string sender, Guid actor, Guid wallet, string key)
    {
        var ownerType = typeof(EthereumProvider).GetNestedType("EthereumCallOwner", BindingFlags.NonPublic)!;
        var feeType = typeof(EthereumProvider).GetNestedType("EthereumFeeAuthorization", BindingFlags.NonPublic)!;
        var owner = Activator.CreateInstance(ownerType, "swap", "operation", "Swap", 0, actor, wallet, null, AzoaScopes.SwapSign, new string('a', 64));
        var fees = Activator.CreateInstance(feeType, new BigInteger(100000), new BigInteger(10_000_000_000),
            new BigInteger(10_000_000_000), new BigInteger(1_000_000_000_000_000), BigInteger.Zero);
        return (Task<AZOAResult<EthereumPreparation>>)typeof(EthereumProvider).GetMethod("ReserveCallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, [key, sender, "0x1111111111111111111111111111111111111111", BigInteger.Zero, new byte[] { 1 },
                "DEX reserved call", null, CancellationToken.None, "immutable-domain-intent", fees, owner])!;
    }

    private static Task<AZOAResult<EthereumPreparation>> Sign(EthereumProvider provider, EthereumPreparation row, SigningContext context)
        => (Task<AZOAResult<EthereumPreparation>>)typeof(EthereumProvider).GetMethod("SignReservedCallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, [row, context, CancellationToken.None])!;
}
