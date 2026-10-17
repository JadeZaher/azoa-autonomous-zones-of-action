using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionEmitReceipt;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionEmitRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_receipt_recovers_without_live_run_or_credential(bool tenant)
    {
        var (input, reference, receipt) = Command(tenant);
        var store = new Mock<IActionEmitStore>(MockBehavior.Strict);
        store.Setup(item => item.GetAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        if (tenant) store.Setup(item => item.HasAcceptedEventAsync(receipt, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var runs = new Mock<IActionRunStore>(MockBehavior.Strict);
        var outcome = await Service(store.Object, runs.Object).ObserveAsync(reference, input);
        outcome.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        outcome.Output.Should().Be(receipt.OutputJson);
        runs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Tenant_receipt_without_original_event_cannot_report_acceptance()
    {
        var (input, reference, receipt) = Command(true);
        var store = new Mock<IActionEmitStore>();
        store.Setup(item => item.GetAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        store.Setup(item => item.HasAcceptedEventAsync(receipt, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (await Service(store.Object, Mock.Of<IActionRunStore>()).ObserveAsync(reference, input))
            .Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
    }

    [Fact]
    public async Task Admission_without_receipt_retries_atomic_acceptance_using_original_credential()
    {
        var (input, reference, receipt) = Command(false);
        var credential = new SigningCredentialWitness(1, "Jwt", reference.ActorUserId, null, null,
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), null, null, [], true,
            TokenUse: AzoaClaims.TokenUseLogin);
        var run = new ActionRun { Id = Guid.ParseExact(receipt.RunId, "N"), UserId = reference.ActorUserId,
            AuthenticationWitnessJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(credential)) };
        var store = new Mock<IActionEmitStore>();
        store.SetupSequence(item => item.AcceptAsync(input, It.IsAny<WorkflowSigningWitness>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Receipt?)null).ReturnsAsync(receipt);
        var runs = new Mock<IActionRunStore>();
        runs.Setup(item => item.GetByIdAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<ActionRun>.Success(run));
        var service = Service(store.Object, runs.Object);
        (await service.ObserveAsync(reference, input)).Status.Should().Be(ActionExternalOperationStatus.Pending);
        (await service.ObserveAsync(reference, input)).Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        store.Verify(item => item.AcceptAsync(input, It.Is<WorkflowSigningWitness>(witness => CanonicalJson.Hash(witness.Credential) == CanonicalJson.Hash(credential)
            && witness.Action.IntentHash == reference.IntentHash), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Changed_reference_and_frozen_bytes_are_refused_without_store_access()
    {
        var (input, reference, _) = Command(false);
        var service = Service(new Mock<IActionEmitStore>(MockBehavior.Strict).Object, new Mock<IActionRunStore>(MockBehavior.Strict).Object);
        (await service.ObserveAsync(reference with { ActorUserId = Guid.NewGuid() }, input)).Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await service.ObserveAsync(reference, input + " ")).Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
    }

    private static ActionEmitService Service(IActionEmitStore store, IActionRunStore runs)
        => new(store, runs, Mock.Of<IActionNodeExecutionStore>(),
            new SigningAdmissionAuthorityFactory(Mock.Of<IUserStore>(), Mock.Of<IWalletStore>(), Mock.Of<ISurrealExecutor>()));

    private static (string Input, ActionExternalOperationReference Reference, Receipt Receipt) Command(bool tenant)
    {
        var run = Guid.NewGuid(); var node = Guid.NewGuid(); var actor = Guid.NewGuid();
        Guid? tenantId = tenant ? Guid.NewGuid() : null;
        var config = JsonSerializer.SerializeToElement(new { Payload = new { message = "hello" } });
        var graph = new ActionDefinition { Nodes = [new() { Id = node, NodeType = ActionNodeType.Emit, Config = config.GetRawText() }] };
        var frozen = new ActionFrozenNodeInput(1, run, node, ActionNodeType.Emit, actor, tenantId, config,
            new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        var input = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(frozen));
        var reference = ActionEmitService.Reference(new(run, node, graph, actor, actingTenantId: tenantId), CanonicalJson.HashCanonical(input));
        var receipt = new Receipt { Id = reference.OperationId!, RunId = run.ToString("N"), NodeId = node.ToString("N"),
            ActorId = actor.ToString("N"), TenantId = tenantId?.ToString("N"), IntentHash = reference.IntentHash,
            OutputJson = "{\"message\":\"hello\"}", EventId = tenant ? reference.OperationId : null,
            RegistrationId = tenant ? Guid.NewGuid().ToString("N") : null, DestinationRevision = tenant ? 1L : null,
            DestinationUrl = tenant ? "https://receiver.example/events" : null };
        return (input, reference, receipt);
    }
}
