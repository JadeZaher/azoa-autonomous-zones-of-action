using System.Numerics;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Providers.Stores.Surreal;

public sealed class SurrealBridgeStoreAmountTests
{
    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    public async Task GetBridge_InvalidPersistedAmount_FailsClosed(string amount)
    {
        var userId = Guid.NewGuid();
        var row = new BridgeTx
        {
            Id = "bridge_tx:bad_amount",
            UserId = $"user:{userId:N}",
            SourceChain = "Solana",
            TargetChain = "Algorand",
            SourceTokenId = "token",
            SourceAddress = "vault",
            TargetAddress = "recipient",
            Amount = amount,
            Status = BridgeTx.StatusKind.Initiated,
            Mode = BridgeTx.ModeKind.Trusted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<BridgeTx>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var store = new SurrealBridgeStore(executor.Object);

        var act = async () => await store.GetBridgeAsync("bad_amount");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*invalid persisted amount*");
    }

    /// <summary>
    /// D13: an amount above <see cref="ulong.MaxValue"/> is no longer corruption — it is
    /// the case the widening exists for. <c>bridge_tx.amount</c> is a string column and
    /// <c>BridgeTransactionResult.Amount</c> is a <c>RailAmount</c>, so a 2^64+ amount
    /// reads back exactly rather than being rejected at the row.
    /// </summary>
    [Theory]
    [InlineData("18446744073709551616")]                                    // ulong.MaxValue + 1
    [InlineData("1000000000000000000000")]                                  // 1000 ETH in wei
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639935")] // 2^256 - 1
    public async Task GetBridge_AmountAboveUlongMax_RoundTripsExactly(string amount)
    {
        var userId = Guid.NewGuid();
        var row = new BridgeTx
        {
            Id = "bridge_tx:wide_amount",
            UserId = $"user:{userId:N}",
            SourceChain = "Ethereum",
            TargetChain = "Algorand",
            SourceTokenId = "token",
            SourceAddress = "vault",
            TargetAddress = "recipient",
            Amount = amount,
            Status = BridgeTx.StatusKind.Initiated,
            Mode = BridgeTx.ModeKind.Trusted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<BridgeTx>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var store = new SurrealBridgeStore(executor.Object);

        var loaded = await store.GetBridgeAsync("wide_amount");

        loaded.Should().NotBeNull();
        loaded!.Amount.ToBaseUnitString().Should().Be(amount);
        loaded.Amount.BaseUnits.Should().Be(BigInteger.Parse(amount));
    }

    [Fact]
    public async Task GetOperation_NegativePersistedAmount_FailsClosed()
    {
        var operationId = Guid.NewGuid();
        var row = new OperationLog
        {
            Id = $"operation_log:{operationId:N}",
            OperationType = "Mint",
            Status = OperationLog.StatusKind.Pending,
            // D13: the column is a decimal base-unit string now, so a negative is not
            // even representable as a number — it is corrupt text, and reading it must
            // still fail closed rather than degrade to zero.
            Amount = "-1",
            CreatedDate = DateTimeOffset.UtcNow,
        };
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var store = new SurrealBridgeStore(executor.Object);

        var act = async () => await store.GetOperationAsync(operationId);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*invalid persisted amount*");
    }
}
