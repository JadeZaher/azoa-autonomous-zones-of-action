using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionReadRecoveryTests
{
    [Fact]
    public async Task LostReadResultUsesFrozenInputAndCurrentCallerAuthorization()
    {
        var actor = Guid.NewGuid(); var target = Guid.NewGuid(); var node = Guid.NewGuid();
        var graph = new ActionDefinition { Id = Guid.NewGuid(), Nodes =
            [new() { Id = node, Name = "Read", NodeType = ActionNodeType.HolonGet, Config = "{}", IsEntry = true }] };
        var run = new ActionRun { Id = Guid.NewGuid(), UserId = actor, ActionId = graph.Id, Status = ActionRunStatus.Running };
        ActionRunSnapshot.Capture(run, graph);
        var input = new ActionFrozenNodeInput(1, run.Id, node, ActionNodeType.HolonGet, actor, null,
            JsonSerializer.SerializeToElement(new IdConfig { Id = target }), new Dictionary<Guid, ActionNodeExecution>(),
            new Dictionary<Guid, ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var reference = new ActionExternalOperationReference(1, "read", $"action:{run.Id:N}:{node:N}:read", node.ToString("N"),
            CanonicalJson.HashCanonical(frozen), actor, null);
        var runs = new Mock<IActionRunStore>();
        runs.Setup(store => store.GetByIdAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<ActionRun>.Success(run));
        var holons = new Mock<IHolonManager>();
        holons.SetupSequence(manager => manager.GetAsync(target, actor, It.IsAny<AZOARequest?>()))
            .ReturnsAsync(AZOAResult<IHolon>.Success(new Holon { Id = target, UserId = actor }))
            .ReturnsAsync(AZOAResult<IHolon>.Failure("Not available to this caller."))
            .ReturnsAsync(AZOAResult<IHolon>.Success(new Holon { Id = target, UserId = actor }));
        using var services = new ServiceCollection().AddSingleton(holons.Object).BuildServiceProvider();
        var recovery = new ActionReadRecovery(services, runs.Object);

        (await recovery.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        (await recovery.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Pending);
        (await recovery.ObserveAsync(reference, frozen)).Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        (await recovery.ObserveAsync(reference with { ActorUserId = Guid.NewGuid() }, frozen)).Status
            .Should().Be(ActionExternalOperationStatus.Ambiguous);
        holons.Verify(manager => manager.GetAsync(target, actor, It.IsAny<AZOARequest?>()), Times.Exactly(3));
        holons.VerifyNoOtherCalls();
    }
}
