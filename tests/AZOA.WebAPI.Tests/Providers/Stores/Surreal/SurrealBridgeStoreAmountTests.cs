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
    [InlineData("18446744073709551616")]
    public async Task GetBridge_InvalidPersistedAmount_FailsClosed(string amount)
    {
        var avatarId = Guid.NewGuid();
        var row = new BridgeTx
        {
            Id = "bridge_tx:bad_amount",
            AvatarId = $"avatar:{avatarId:N}",
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

    [Fact]
    public async Task GetOperation_NegativePersistedAmount_FailsClosed()
    {
        var operationId = Guid.NewGuid();
        var row = new OperationLog
        {
            Id = $"operation_log:{operationId:N}",
            OperationType = "Mint",
            Status = OperationLog.StatusKind.Pending,
            Amount = -1,
            CreatedDate = DateTimeOffset.UtcNow,
        };
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var store = new SurrealBridgeStore(executor.Object);

        var act = async () => await store.GetOperationAsync(operationId);

        await act.Should().ThrowAsync<OverflowException>();
    }
}
