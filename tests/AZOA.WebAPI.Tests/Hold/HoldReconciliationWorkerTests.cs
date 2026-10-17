using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Hold;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Hold;

public sealed class HoldReconciliationWorkerTests
{
    [Fact]
    public async Task ScanContinuesAfterDependencyFailureAndRebroadcastsOnlySavedAuthorizedWire()
    {
        var store = new Mock<IHoldCommandStore>();
        var module = new Mock<IValueTransferTransactionModule>(MockBehavior.Strict);
        var provider = new Mock<IBlockchainProvider>();
        provider.Setup(value => value.TryGetModule<IValueTransferTransactionModule>(out It.Ref<IValueTransferTransactionModule?>.IsAny))
            .Returns((out IValueTransferTransactionModule? value) => { value = module.Object; return true; });
        var factory = new Mock<IBlockchainProviderFactory>();
        factory.Setup(value => value.GetProvider("Algorand", ChainNetwork.Devnet)).Returns(provider.Object);
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "quote", new("Algorand", ChainNetwork.Devnet, "42", "sender", "recipient", "1", "sender"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5));
        var signing = SigningContext.PlatformFor("Algorand");
        var intent = new ValueTransferIntent("Algorand", ChainNetwork.Devnet, "42", "sender", "recipient", "1", signing, "prepared-key");
        var wire = new PreparedValueTransfer("saved-tx", "saved-signed-bytes", "genesis", intent);
        HoldCommand Row(string id) => new() { Id = id, State = "Submitted", PreparedJson = JsonSerializer.Serialize(wire),
            RailPlanJson = JsonSerializer.Serialize(new HoldRailPlan(1, "Release", id, 0, quote, null, null, signing)) };
        var unavailable = Row("unavailable"); var admitted = Row("admitted"); var raced = Row("raced");
        store.Setup(value => value.ListReconciliationAsync(It.IsAny<DateTimeOffset>(), 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { unavailable, admitted, raced });
        store.Setup(value => value.ScheduleReconciliationAsync(It.IsAny<HoldCommand>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HoldCommand row, DateTimeOffset _, CancellationToken _) => row.Id != "raced");
        module.SetupSequence(value => value.ObserveValueTransferAsync(It.IsAny<PreparedValueTransfer>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("dependency outage"))
            .ReturnsAsync(AZOAResult<ChainConfirmation>.Success(ChainConfirmation.Unknown));
        module.Setup(value => value.SubmitValueTransferAsync(It.Is<PreparedValueTransfer>(saved => saved == wire), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddSingleton(store.Object).AddSingleton(factory.Object).AddScoped<HoldRailExecutor>();
        using var scope = services.BuildServiceProvider();
        var worker = new HoldReconciliationWorker(scope.GetRequiredService<IServiceScopeFactory>(), NullLogger<HoldReconciliationWorker>.Instance);
        await worker.SweepAsync();
        module.Verify(value => value.SubmitValueTransferAsync(It.Is<PreparedValueTransfer>(saved => saved == wire), It.IsAny<CancellationToken>()), Times.Once);
        module.Verify(value => value.PrepareValueTransferAsync(It.IsAny<ValueTransferIntent>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(value => value.AuthorizeSendAsync(It.IsAny<HoldCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
