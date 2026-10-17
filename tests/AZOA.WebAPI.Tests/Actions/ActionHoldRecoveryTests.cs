using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Providers.Blockchain;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Models.Responses;
using HoldCommand = AZOA.WebAPI.Persistence.SurrealDb.Models.HoldCommand;
using HoldCommandReceipt = AZOA.WebAPI.Persistence.SurrealDb.Models.HoldCommandReceipt;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionHoldRecoveryTests
{
    [Fact]
    public async Task FrozenExplicitNetworkDoesNotReadChangedProviderDefault()
    {
        var fixture = new Fixture(new HoldNodeConfig { ChainType = "Algorand", Network = ChainNetwork.Testnet,
            AssetId = "7", Amount = "4", ReservePartition = "action", AutoRelease = false });
        fixture.Authorize();
        var provider = new Mock<IBlockchainProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.ChainType).Returns("Algorand");
        provider.SetupGet(x => x.ActiveNetwork).Returns(ChainNetwork.Testnet);
        var factory = new Mock<IBlockchainProviderFactory>(MockBehavior.Strict);
        factory.Setup(x => x.GetProvider("Algorand", ChainNetwork.Testnet)).Returns(provider.Object);
        var reserve = new Mock<IPlatformReserveLedgerStore>(MockBehavior.Strict);
        var key = AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger.RecordIdFor("Algorand", "Testnet", "7");
        reserve.Setup(x => x.GetAsync(key, It.IsAny<CancellationToken>())).ReturnsAsync(
            AZOAResult<AZOA.WebAPI.Persistence.SurrealDb.Models.PlatformReserveLedger?>.Success(null));
        var service = new ActionHoldOperationService(fixture.Holds.Object, null!, null!, fixture.Executions.Object, null!, reserve.Object, factory.Object);

        (await service.ObserveAsync(fixture.Reference, fixture.Frozen)).Status.Should().Be(ActionExternalOperationStatus.Pending);

        factory.VerifyGet(x => x.DefaultNetwork, Times.Never);
        reserve.VerifyAll();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NamedSourceRejectsAnotherRunOrMismatchingReceiptOutcome(bool wrongRun, bool wrongReceipt)
    {
        var source = Guid.NewGuid();
        var predecessor = new ActionNodeExecution { NodeId = source, State = ActionNodeState.Succeeded };
        var fixture = new Fixture(new ReleaseNodeConfig { ChainType = "Algorand", HoldNodeName = "source" },
            ActionNodeType.Release, new Dictionary<Guid, ActionNodeExecution> { [source] = predecessor },
            (run, actor) =>
            {
                predecessor.RunId = wrongRun ? Guid.NewGuid() : run;
                predecessor.Output = JsonSerializer.Serialize(new { OperationId = HoldCommandIdentity.Operation(actor, null, $"action:{run:N}:{source:N}:hold") });
            });
        fixture.Authorize();
        var graph = new ActionDefinition { Id = Guid.NewGuid(), Nodes = [new() { Id = source, Name = "source", NodeType = ActionNodeType.Hold }] };
        var run = new ActionRun { Id = fixture.Execution.RunId };
        ActionRunSnapshot.Capture(run, graph);
        var runs = new Mock<IActionRunStore>(MockBehavior.Strict);
        runs.Setup(x => x.GetByIdAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<ActionRun>.Success(run));
        var operationId = HoldCommandIdentity.Operation(fixture.Reference.ActorUserId, null, $"action:{run.Id:N}:{source:N}:hold");
        if (wrongReceipt)
        {
            var actor = fixture.Reference.ActorUserId;
            var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Testnet, "genesis", "7", "reserve");
            var outcome = new HoldCommandView("different-operation", 1, "Open", "Completed", "liability", "human", asset, "4", actor, actor, null, actor, null, null);
            fixture.Holds.Setup(x => x.GetReceiptAsync(operationId, It.IsAny<CancellationToken>())).ReturnsAsync(new HoldCommandReceipt {
                Id = operationId, ActorUserId = actor.ToString("N"), OutcomeJson = JsonSerializer.Serialize(outcome) });
        }
        var service = new ActionHoldOperationService(fixture.Holds.Object, null!, null!, fixture.Executions.Object, runs.Object, null!, null!);

        (await service.ObserveAsync(fixture.Reference, fixture.Frozen)).Status.Should().Be(ActionExternalOperationStatus.Pending);

        fixture.Holds.Verify(x => x.GetHoldAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        if (wrongRun) fixture.Holds.Verify(x => x.GetReceiptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidFrozenOpeningPersistsRejectionBeforeReportingFailureAndReplaysAfterCrash()
    {
        var fixture = new Fixture(new HoldNodeConfig { ChainType = "Algorand", Network = ChainNetwork.Testnet, AssetId = "7", Amount = "0", ReservePartition = "action", AutoRelease = false });
        var durable = fixture.Execution;
        fixture.Executions.Setup(x => x.GetByRunAndNodeAsync(durable.RunId, durable.NodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<ActionNodeExecution>.Success(durable));
        fixture.Executions.Setup(x => x.UpdateAsync(It.IsAny<ActionNodeExecution>(), ActionNodeState.Running, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActionNodeExecution candidate, ActionNodeState? _, CancellationToken _) =>
            {
                candidate.Version.Should().Be(durable.Version);
                candidate.FrozenInput.Should().Be(durable.FrozenInput);
                candidate.ExternalOperation.Should().Be(durable.ExternalOperation);
                candidate.RecoveryState.Should().Be("HoldAdmissionRejected");
                durable = candidate.Clone(); durable.Version++;
                return AZOAResult<ActionNodeExecution>.Success(durable);
            });

        var first = await fixture.Service.ObserveAsync(fixture.Reference, fixture.Frozen);
        var recovered = await fixture.Service.ObserveAsync(fixture.Reference, fixture.Frozen);

        first.Status.Should().Be(ActionExternalOperationStatus.Failed);
        recovered.Status.Should().Be(ActionExternalOperationStatus.Failed);
        recovered.Detail.Should().Be(first.Detail);
        durable.State.Should().Be(ActionNodeState.Failed);
        fixture.Executions.Verify(x => x.UpdateAsync(It.IsAny<ActionNodeExecution>(), ActionNodeState.Running, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Holds.Verify(x => x.GetCommandAsync(fixture.Reference.OperationId!, It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Holds.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LostRejectionCasDoesNotInventTerminalFailure()
    {
        var fixture = new Fixture(new HoldNodeConfig { Amount = "-1" });
        fixture.Executions.Setup(x => x.GetByRunAndNodeAsync(fixture.Execution.RunId, fixture.Execution.NodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(fixture.Execution));
        fixture.Executions.Setup(x => x.UpdateAsync(It.IsAny<ActionNodeExecution>(), ActionNodeState.Running, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Failure("Version changed"));

        var result = await fixture.Service.ObserveAsync(fixture.Reference, fixture.Frozen);

        result.Status.Should().Be(ActionExternalOperationStatus.Pending);
        fixture.Execution.State.Should().Be(ActionNodeState.Running);
        fixture.Execution.RecoveryState.Should().NotBe("HoldAdmissionRejected");
    }

    [Fact]
    public async Task MissingOriginalSigningAuthorityRemainsAmbiguousWithoutRejectingOrDrawingReserve()
    {
        var fixture = new Fixture(new HoldNodeConfig { ChainType = "Algorand", Network = ChainNetwork.Testnet, AssetId = "7", Amount = "4", ReservePartition = "action", AutoRelease = false });
        fixture.Executions.Setup(x => x.GetByRunAndNodeAsync(fixture.Execution.RunId, fixture.Execution.NodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(fixture.Execution));

        var result = await fixture.Service.ObserveAsync(fixture.Reference, fixture.Frozen);

        result.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        fixture.Executions.Verify(x => x.UpdateAsync(It.IsAny<ActionNodeExecution>(), It.IsAny<ActionNodeState?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("AwaitingConsent", false, ActionExternalOperationStatus.Pending)]
    [InlineData("Completed", false, ActionExternalOperationStatus.Ambiguous)]
    [InlineData("Completed", true, ActionExternalOperationStatus.Succeeded)]
    public async Task RecoveryRequiresActualAccountingReceiptAndNeverObtainsFreshSignatures(string state, bool hasReceipt, ActionExternalOperationStatus expected)
    {
        var fixture = new Fixture(new HoldNodeConfig { ChainType = "Algorand", Network = ChainNetwork.Testnet, AssetId = "7", Amount = "4", ReservePartition = "action", AutoRelease = false });
        var actor = fixture.Reference.ActorUserId;
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Testnet, "genesis", "7", "reserve");
        var definition = new HoldDefinition(asset, "4", "Reserve", actor, null, "action", null, false, null);
        var intent = new HoldCommandIntent(1, fixture.Reference.RequestKey, "Open", "original", actor, actor, null, null, asset, definition);
        var credential = new SigningCredentialWitness(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
            null, null, [], true, TokenUse: "login");
        var witness = new WorkflowSigningWitness(1, credential, new(fixture.Execution.RunId, fixture.Execution.NodeId, actor, null,
            fixture.Reference.IntentHash), [AzoaScopes.TransferSign], []);
        var row = new HoldCommand { Id = fixture.Reference.OperationId!, ActorUserId = actor.ToString("N"), State = state,
            Kind = "Open", IntentJson = JsonSerializer.Serialize(intent), IntentHash = CanonicalJson.Hash(intent),
            AuthorityJson = JsonSerializer.Serialize(new HoldCommandAuthority(1, Workflow: witness)) };
        fixture.Holds.Setup(x => x.GetCommandAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(row);
        var view = new HoldCommandView(row.Id, 2, "Open", "Completed", "liability", "original", asset, "4", actor, actor, null, actor, null, null);
        fixture.Holds.Setup(x => x.GetReceiptAsync(row.Id, It.IsAny<CancellationToken>())).ReturnsAsync(hasReceipt
            ? new HoldCommandReceipt { Id = row.Id, ActorUserId = row.ActorUserId, IntentHash = row.IntentHash, OutcomeJson = JsonSerializer.Serialize(view) }
            : null);

        var result = await fixture.Service.ObserveAsync(fixture.Reference, fixture.Frozen);

        result.Status.Should().Be(expected);
        fixture.Executions.VerifyNoOtherCalls();
        if (hasReceipt)
        {
            using var output = JsonDocument.Parse(result.Output!);
            output.RootElement.GetProperty("HoldRecordId").GetString().Should().Be("liability");
            output.RootElement.GetProperty("OperationId").GetString().Should().Be(row.Id);
            output.RootElement.GetProperty("State").GetString().Should().Be("Held");
        }
        else result.Output.Should().BeNull();
    }

    private sealed class Fixture
    {
        public Mock<IHoldCommandStore> Holds { get; } = new(MockBehavior.Strict);
        public Mock<IActionNodeExecutionStore> Executions { get; } = new(MockBehavior.Strict);
        public ActionNodeExecution Execution { get; }
        public ActionExternalOperationReference Reference { get; }
        public string Frozen { get; }
        public ActionHoldOperationService Service { get; }

        public void Authorize()
        {
            var actor = Reference.ActorUserId;
            var credential = new SigningCredentialWitness(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
                null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
            Execution.SigningAuthorityJson = JsonSerializer.Serialize(new WorkflowSigningWitness(1, credential,
                new(Execution.RunId, Execution.NodeId, actor, null, Reference.IntentHash), [AzoaScopes.TransferSign], []));
            Executions.Setup(x => x.GetByRunAndNodeAsync(Execution.RunId, Execution.NodeId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<ActionNodeExecution>.Success(Execution));
        }

        public Fixture(object config, ActionNodeType type = ActionNodeType.Hold,
            IReadOnlyDictionary<Guid, ActionNodeExecution>? prior = null, Action<Guid, Guid>? configure = null)
        {
            var actor = Guid.NewGuid(); var run = Guid.NewGuid(); var node = Guid.NewGuid();
            configure?.Invoke(run, actor);
            var input = new ActionFrozenNodeInput(1, run, node, type, actor, null,
                JsonSerializer.SerializeToElement(config), new Dictionary<Guid, ActionNodeExecution>(), prior ?? new Dictionary<Guid, ActionNodeExecution>());
            Frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
            var key = $"action:{run:N}:{node:N}:hold";
            Reference = new(1, "hold", key, HoldCommandIdentity.Operation(actor, null, key), CanonicalJson.HashCanonical(Frozen), actor, null);
            Execution = new() { Id = Guid.NewGuid(), RunId = run, NodeId = node, State = ActionNodeState.Running,
                Version = 4, ExternalOperation = Reference, FrozenInput = Frozen };
            Holds.Setup(x => x.GetCommandAsync(Reference.OperationId!, It.IsAny<CancellationToken>())).ReturnsAsync((HoldCommand?)null);
            Service = new(Holds.Object, null!, null!, Executions.Object, null!, null!, null!);
        }
    }
}
