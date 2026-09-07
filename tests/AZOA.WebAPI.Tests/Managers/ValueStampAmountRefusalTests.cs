// SPDX-License-Identifier: UNLICENSED

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// D16 on the QUANTITY the stamp is computed from: Burn and Transfer read their amount out of
/// the <c>Amount</c> parameter, and when it is absent or unreadable the operation must settle
/// UNPRICED rather than be stamped against a defaulted count.
///
/// <para>The execution paths may legitimately default a missing amount (a transfer of one
/// asset). The value stamp may not: a fiat figure computed from an invented quantity is
/// indistinguishable, on the wire and in a tax export, from one computed from a real one —
/// "a fabricated, defaulted, or stale-presented-as-current rate on a financial record is worse
/// than an absent one" (<c>Persistence/SurrealDb/Models/AGENTS.md</c> §value-stamp).</para>
/// </summary>
public sealed class ValueStampAmountRefusalTests
{
    [Fact]
    public async Task Transfer_WithNoRecordedAmount_SettlesUnpriced_RatherThanStampingADefaultedOne()
    {
        // The defaulted quantity was 1 asset, which at 250 c/unit stamps a real-looking $2.50.
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(TransferringProvider(), op => persisted = op, Quoting(250m));

        await manager.ExecuteAsync(TransferOp(amount: null));

        persisted!.Status.Should().Be(OperationStatus.Transferred, "the chain effect still happened");
        persisted.ValueStamp.Should().BeNull(
            "no quantity was recorded, so there is nothing to price — and a defaulted 1 is a fabrication");
    }

    [Fact]
    public async Task Burn_WithNoRecordedAmount_SettlesUnpriced_RatherThanStampingAZero()
    {
        // The defaulted quantity was 0, which stamps MinorUnits "0" — a real quote of zero,
        // which is precisely the state §value-stamp keeps distinct from "never priced".
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(BurningProvider(), op => persisted = op, Quoting(250m));

        await manager.ExecuteAsync(BurnOp(amount: null));

        persisted!.Status.Should().Be(OperationStatus.Burned);
        persisted.ValueStamp.Should().BeNull("an absent quantity must read back unpriced, never as a stamped zero");
    }

    [Fact]
    public async Task Transfer_WithAnUnreadableAmount_SettlesUnpriced()
    {
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(TransferringProvider(), op => persisted = op, Quoting(250m));

        await manager.ExecuteAsync(TransferOp(amount: "one hundred"));

        persisted!.ValueStamp.Should().BeNull();
    }

    [Fact]
    public async Task Transfer_WithARecordedAmount_IsStillStampedAgainstThatRealQuantity()
    {
        // The refusal must cost nothing on the path that has a real figure to price.
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(TransferringProvider(), op => persisted = op, Quoting(250m));

        await manager.ExecuteAsync(TransferOp(amount: "4"));

        persisted!.ValueStamp.Should().NotBeNull();
        persisted.ValueStamp!.MinorUnits.Should().Be("1000", "4 base units at 250 minor units each");
    }

    [Fact]
    public async Task Transfer_WithARecordedZeroAmount_IsStampedAtZero_BecauseZeroIsAFact()
    {
        // A recorded zero is a real quantity: it stays stampable, exactly as a rate of "0"
        // stays a real quote. Only the ABSENCE of a figure refuses the stamp.
        IBlockchainOperation? persisted = null;
        var manager = BuildManager(TransferringProvider(), op => persisted = op, Quoting(250m));

        await manager.ExecuteAsync(TransferOp(amount: "0"));

        persisted!.ValueStamp.Should().NotBeNull();
        persisted.ValueStamp!.MinorUnits.Should().Be("0");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private sealed class StubRateSource(decimal rate) : IValueRateSource
    {
        public ValueTask<ValueRateQuote?> TryQuoteAsync(
            ValueDimension dimension, string chainType, DateTimeOffset requestedAt, CancellationToken ct = default)
            => ValueTask.FromResult<ValueRateQuote?>(
                new ValueRateQuote(ValueDimension.Usd, rate, DateTimeOffset.UnixEpoch, "test-source"));
    }

    private static IValueRateSource Quoting(decimal rate) => new StubRateSource(rate);

    private static Mock<IBlockchainProvider> TransferringProvider()
    {
        var provider = ChainProvider();
        provider.Setup(p => p.TransferAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<RailAmount>(), It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx" });
        return provider;
    }

    private static Mock<IBlockchainProvider> BurningProvider()
    {
        var provider = ChainProvider();
        provider.Setup(p => p.BurnAsync(
                    It.IsAny<string>(), It.IsAny<RailAmount>(), It.IsAny<string>(),
                    It.IsAny<SigningContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AZOAResult<string> { Result = "algo_tx" });
        return provider;
    }

    private static Mock<IBlockchainProvider> ChainProvider()
    {
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(p => p.ChainType).Returns("Algorand");
        return provider;
    }

    private static BlockchainOperationManager BuildManager(
        Mock<IBlockchainProvider> provider,
        Action<IBlockchainOperation> capture,
        IValueRateSource rateSource)
    {
        var store = new Mock<IBlockchainOperationStore>();
        store.Setup(s => s.UpsertAsync(It.IsAny<IBlockchainOperation>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IBlockchainOperation op, CancellationToken _) =>
             {
                 capture(op);
                 return new AZOAResult<IBlockchainOperation> { Result = op };
             });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultChain"] = "Algorand",
                ["Blockchain:DefaultNetwork"] = "Devnet",
                ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            })
            .Build();

        var factory = new BlockchainProviderFactory(
            [new BlockchainProviderRegistration("Algorand", () => provider.Object)],
            config);

        return new BlockchainOperationManager(
            store.Object, factory, new FakeIdempotencyStore(), rateSource);
    }

    private static BlockchainOperation TransferOp(string? amount) => Op("Transfer", amount, op =>
        op.RecipientAddress = "ALGORECIPIENT");

    private static BlockchainOperation BurnOp(string? amount) => Op("Burn", amount, _ => { });

    private static BlockchainOperation Op(string type, string? amount, Action<BlockchainOperation> tweak)
    {
        var operation = new BlockchainOperation
        {
            OperationType = type,
            AssetType = "FT",
            Parameters = new Dictionary<string, string>
            {
                ["ChainType"] = "Algorand",
                ["WalletAddress"] = "ALGOTESTADDRESS",
                ["TokenId"] = "1001",
                ["SourceTokenId"] = "1001",
            },
        };

        if (amount is not null)
            operation.Parameters["Amount"] = amount;

        tweak(operation);
        return operation;
    }
}
