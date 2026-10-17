using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Tests.Managers;

public class BlockchainNativeOperationTests
{
    [Theory]
    [InlineData("DeployContract", "Deploy", "Deployed")]
    [InlineData("CallContract", "Call", "Called")]
    public async Task Pending_receipt_never_becomes_success_until_authoritative_read(string type, string kind, string terminal)
    {
        var actor = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var wallet = Guid.NewGuid();
        var id = DeploymentIdentity.Operation(actor, tenant, "request-one");
        var target = new DeploymentTarget("NativeContract", "Solana", "Devnet");
        var intent = new NativeOperationIntent(1, id, kind, "Solana", "Devnet", "local-genesis", "artifact-hash",
            JsonSerializer.SerializeToElement(new { }), [new NativeAuthority("caller-key", SigningContext.ForUser(actor, wallet))],
            new NativeBudget("10000", "1000000", "0", "quote-id"));
        var start = new DeploymentStartRequest("artifact", "artifact-hash", target, intent);
        var receipt = new DeploymentReceipt(id, "artifact", "artifact-hash", target, "Submitted", "Deploy", null, null, ["actual-wire-id"], null, 1);
        var deployments = new Mock<IDeploymentService>(MockBehavior.Strict);
        deployments.Setup(x => x.StartAsync(actor, tenant, "request-one", It.IsAny<DeploymentStartRequest>(), default))
            .ReturnsAsync(AZOAResult<DeploymentReceipt>.Success(receipt));
        deployments.Setup(x => x.GetAsync(id, actor, tenant, default))
            .ReturnsAsync(AZOAResult<DeploymentReceipt>.Success(receipt with { State = "Confirmed", Version = 2 }));
        var store = new Mock<IBlockchainOperationStore>(MockBehavior.Strict);
        IBlockchainOperation? saved = null;
        store.Setup(x => x.UpsertAsync(It.IsAny<IBlockchainOperation>(), default))
            .ReturnsAsync((IBlockchainOperation row, CancellationToken _) => { saved = row; return AZOAResult<IBlockchainOperation>.Success(row); });
        store.Setup(x => x.GetByIdAsync(Guid.ParseExact(id, "N"), default))
            .ReturnsAsync(() => AZOAResult<IBlockchainOperation>.Success(saved!));
        var manager = new BlockchainOperationManager(store.Object, Mock.Of<IBlockchainProviderFactory>(), new FakeIdempotencyStore(), deployments: deployments.Object);
        var operation = new BlockchainOperation { UserId = actor, InitiatorUserId = actor, ActingTenantId = tenant,
            WalletId = wallet, IdempotencyKey = "request-one", OperationType = type,
            Parameters = new() { ["DeploymentRequest"] = JsonSerializer.Serialize(start) } };

        var pending = await manager.ExecuteAsync(operation);
        pending.IsError.Should().BeFalse();
        pending.Result!.Status.Should().Be("Submitted");
        pending.Result.CompletedDate.Should().BeNull();
        pending.Result.Parameters["TxHash"].Should().Be("actual-wire-id");
        var forbidden = await manager.GetAsync(operation.Id, Guid.NewGuid());
        forbidden.IsError.Should().BeTrue();
        deployments.Verify(x => x.GetAsync(id, actor, tenant, default), Times.Never);
        var confirmed = await manager.GetAsync(operation.Id, actor);
        confirmed.Result!.Status.Should().Be(terminal);
        deployments.Verify(x => x.StartAsync(actor, tenant, "request-one", It.IsAny<DeploymentStartRequest>(), default), Times.Once);
    }
}
