using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using Moq;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Tests.Providers.Stores.Surreal;

public sealed class SurrealBlockchainOperationReceiptStoreTests
{
    [Fact]
    public async Task GetByIdempotencyKey_RoundTripsReceiptCorrelationAndInitiators()
    {
        var operationId = Guid.NewGuid();
        var initiatorAvatarId = Guid.NewGuid();
        var initiatorApiKeyId = Guid.NewGuid();
        var correlation = new string('a', 64);
        var row = new OperationLog
        {
            Id = operationId.ToString("N"),
            OperationType = "Mint",
            Status = OperationLog.StatusKind.Pending,
            IdempotencyKey = correlation,
            InitiatorAvatarId = $"avatar:{initiatorAvatarId:N}",
            InitiatorApiKeyId = $"api_key:{initiatorApiKeyId:N}",
            CreatedDate = DateTimeOffset.UtcNow,
        };
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        var store = new SurrealBlockchainOperationStore(executor.Object);

        var result = await store.GetByIdempotencyKeyAsync(correlation);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.Id.Should().Be(operationId);
        result.Result.IdempotencyKey.Should().Be(correlation);
        result.Result.InitiatorAvatarId.Should().Be(initiatorAvatarId);
        result.Result.InitiatorApiKeyId.Should().Be(initiatorApiKeyId);
    }

    [Fact]
    public async Task GetByIdempotencyKey_BlankCorrelation_ReturnsErrorWithoutQuerying()
    {
        var executor = new Mock<ISurrealExecutor>();
        var store = new SurrealBlockchainOperationStore(executor.Object);

        var result = await store.GetByIdempotencyKeyAsync(" ");

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Idempotency key is required.");
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        executor.Verify(candidate => candidate.QuerySingleAsync<OperationLog>(
            It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetByIdempotencyKey_StorageFailure_ReturnsSafeError()
    {
        var correlation = new string('c', 64);
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage detail"));
        var store = new SurrealBlockchainOperationStore(executor.Object);

        var result = await store.GetByIdempotencyKeyAsync(correlation);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Operation lookup is temporarily unavailable.");
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Message.Should().NotContain(correlation);
    }

    [Fact]
    public async Task GetByIdempotencyKey_MissingCorrelation_ReturnsNotFound()
    {
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationLog?)null);
        var store = new SurrealBlockchainOperationStore(executor.Object);

        var result = await store.GetByIdempotencyKeyAsync(new string('d', 64));

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Operation not found.");
        result.Code.Should().Be(AzoaErrorCodes.NotFound);
    }

    [Fact]
    public async Task UpsertExistingOperation_ChangedReceiptProvenance_IsRejectedWithoutWriting()
    {
        var operationId = Guid.NewGuid();
        var executor = new Mock<ISurrealExecutor>();
        executor.Setup(candidate => candidate.QuerySingleAsync<OperationLog>(
                It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationLog
            {
                Id = operationId.ToString("N"),
                OperationType = "Mint",
                Status = OperationLog.StatusKind.Pending,
                IdempotencyKey = new string('e', 64),
                InitiatorAvatarId = $"avatar:{Guid.NewGuid():N}",
                CreatedDate = DateTimeOffset.UtcNow,
            });
        var store = new SurrealBlockchainOperationStore(executor.Object);

        var result = await store.UpsertAsync(new BlockchainOperation
        {
            Id = operationId,
            OperationType = "Mint",
            Status = OperationStatus.Completed,
            IdempotencyKey = new string('f', 64),
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Be("Operation receipt provenance cannot be changed.");
        result.Code.Should().Be(AzoaErrorCodes.InvalidRequest);
        executor.Verify(candidate => candidate.ExecuteAsync(
            It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
