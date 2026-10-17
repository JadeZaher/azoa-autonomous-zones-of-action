using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using NftWorkflowCostAcceptanceRecord = AZOA.WebAPI.Persistence.SurrealDb.Models.NftWorkflowCostAcceptanceRecord;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class NftWorkflowCostConsentTests
{
    [Theory]
    [InlineData(4, "Unresolved")]
    [InlineData(5, "Obsolete")]
    public async Task MissingReceiptRequiresExecutionVersionEvidence(long currentVersion, string expected)
    {
        var actor = Guid.NewGuid();
        var request = Request();
        var execution = new ActionNodeExecution { Id = request.ExecutionId, Version = currentVersion,
            ExternalOperation = new(1, "nft", "request", "lifecycle", "frozen", actor, null) };
        var executions = new Mock<IActionNodeExecutionStore>(MockBehavior.Strict);
        var audits = new Mock<INftWorkflowCostAcceptanceStore>(MockBehavior.Strict);
        var sequence = new MockSequence();
        executions.InSequence(sequence).Setup(x => x.GetByIdAsync(request.ExecutionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(execution));
        audits.InSequence(sequence).Setup(x => x.GetAsync(actor, null, request.ExecutionId, request.RequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NftWorkflowCostAcceptanceRecord?)null);
        var service = Service(executions.Object, audits.Object, Mock.Of<INftLifecycleService>());

        var result = await service.ReconcileAsync(request, actor, null);

        result.Result!.State.Should().Be(expected);
        executions.VerifyAll();
        audits.VerifyAll();
    }

    [Fact]
    public async Task CommittedReceiptWinsAfterExecutionDisappearsWithoutFreshAuthority()
    {
        var actor = Guid.NewGuid();
        var request = Request();
        var executions = new Mock<IActionNodeExecutionStore>();
        executions.Setup(x => x.GetByIdAsync(request.ExecutionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Failure("Unavailable"));
        var audits = new Mock<INftWorkflowCostAcceptanceStore>();
        audits.Setup(x => x.GetAsync(actor, null, request.ExecutionId, request.RequestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NftWorkflowCostAcceptanceRecord { LifecycleId = "original", FrozenInputHash = "frozen",
                RequestHash = NftWorkflowCostAcceptanceIdentity.RequestHash(request, "original", "frozen") });
        var lifecycle = new Mock<INftLifecycleService>(MockBehavior.Strict);
        lifecycle.Setup(x => x.GetAsync(actor, "original", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<NftLifecycleView>.Success(new("original", Guid.NewGuid(), "Preparing", "Create", null, null, null)));

        var result = await Service(executions.Object, audits.Object, lifecycle.Object).ReconcileAsync(request, actor, null);

        result.Result!.State.Should().Be("Applied");
        result.Result.Lifecycle!.OperationId.Should().Be("original");
        lifecycle.VerifyAll();
    }

    private static NftWorkflowCostAcceptance Request()
        => new(Guid.NewGuid(), Guid.NewGuid(), 4, new("quote", "hash", new("1", "2", "3")));

    private static NftWorkflowCostConsentService Service(IActionNodeExecutionStore executions,
        INftWorkflowCostAcceptanceStore audits, INftLifecycleService lifecycle)
        => new(executions, Mock.Of<IActionRunStore>(), lifecycle, Mock.Of<INftProtocolCostQuoteStore>(), audits,
            null!, TimeProvider.System);
}
