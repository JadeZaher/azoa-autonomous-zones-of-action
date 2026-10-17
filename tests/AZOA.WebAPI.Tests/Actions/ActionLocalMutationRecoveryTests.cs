using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionLocalReceipt;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionLocalMutationRecoveryTests
{
    [Fact]
    public async Task RetryablePlanningFailureDoesNotPinFailedReceiptAndCanRecover()
    {
        var actor = Guid.NewGuid(); var target = Guid.NewGuid(); var run = Guid.NewGuid(); var node = Guid.NewGuid();
        var config = JsonSerializer.SerializeToElement(new HolonUpdateNodeConfig
            { HolonId = target, Model = new HolonUpdateModel { Name = "Updated" } });
        var graph = new ActionDefinition { Nodes = [new() { Id = node, NodeType = ActionNodeType.HolonUpdate, Config = config.GetRawText() }] };
        var input = new ActionFrozenNodeInput(1, run, node, ActionNodeType.HolonUpdate, actor, null, config,
            new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var reference = ActionLocalMutationService.Reference(new(run, node, graph, actor), CanonicalJson.HashCanonical(frozen));
        var holons = new Mock<IHolonStore>();
        holons.SetupSequence(store => store.GetByIdAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<IHolon>.Failure("Temporary read conflict."))
            .ReturnsAsync(AZOAResult<IHolon>.Success(new Holon { Id = target, UserId = actor, Name = "Original", IsActive = true }));
        var mutations = new Mock<IActionLocalMutationStore>();
        mutations.Setup(store => store.CommitAsync(It.IsAny<ActionLocalMutationPlan>(), It.IsAny<Receipt>(),
                It.IsAny<long?>(), false, It.IsAny<CancellationToken>()))
            .Returns((ActionLocalMutationPlan plan, Receipt receipt, long? policy, bool wallet, CancellationToken ct) =>
            {
                receipt.IsError.Should().BeFalse();
                plan.HolonWrites[target]!.Name.Should().Be("Updated");
                return Task.FromResult(AZOAResult<Receipt>.Success(receipt));
            });
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new ActionLocalMutationService(services, holons.Object, Mock.Of<IWalletStore>(), mutations.Object,
            Mock.Of<IActionNodeExecutionStore>(), Mock.Of<ISurrealExecutor>());

        (await service.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        mutations.Verify(store => store.CommitAsync(It.IsAny<ActionLocalMutationPlan>(), It.IsAny<Receipt>(),
            It.IsAny<long?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        (await service.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        mutations.Verify(store => store.CommitAsync(It.IsAny<ActionLocalMutationPlan>(), It.IsAny<Receipt>(),
            It.IsAny<long?>(), false, It.IsAny<CancellationToken>()), Times.Once);
        holons.Verify(store => store.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
