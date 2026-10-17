using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Signing;
using FluentAssertions;
using Moq;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionEmitReceipt;

namespace AZOA.WebAPI.Tests.Actions.Handlers;

public sealed class EmitNodeHandlerTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"currency\":\"USD\",\"freelancerPayout\":\"500\"}", true)]
    public async Task Admitted_emit_projects_exact_accepted_payload(string payload, bool tenant)
    {
        var (handler, context, receipts, reference) = Fixture(payload, tenant);
        var receipt = new Receipt { Id = reference.OperationId!, RunId = context.RunId.ToString("N"),
            NodeId = context.NodeId.ToString("N"), ActorId = context.ActingUserId.ToString("N"),
            TenantId = context.ActingTenantId?.ToString("N"), IntentHash = reference.IntentHash, OutputJson = payload,
            EventId = tenant ? reference.OperationId : null, RegistrationId = tenant ? Guid.NewGuid().ToString("N") : null,
            DestinationRevision = tenant ? 1L : null, DestinationUrl = tenant ? "https://receiver.example/events" : null };
        receipts.Setup(store => store.GetAsync(receipt.Id, It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        receipts.Setup(store => store.HasAcceptedEventAsync(receipt, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await handler.HandleAsync(context);
        handler.NodeType.Should().Be(ActionNodeType.Emit);
        ((IActionNodeHandler)handler).RequiresChainCapability.Should().BeFalse();
        result.PendingOperation!.Status.Should().Be(ActionExternalOperationStatus.Succeeded);
        result.Output.Should().Be(payload);
    }

    [Fact]
    public async Task Missing_durable_admission_cannot_pass_through_success()
    {
        var (handler, context, _, _) = Fixture("{}", false, admitted: false);
        var result = await handler.HandleAsync(context);
        result.IsError.Should().BeTrue();
        result.Retriable.Should().BeFalse();
        result.PendingOperation.Should().BeNull();
    }

    [Fact]
    public async Task Infrastructure_failure_is_not_swallowed_as_success()
    {
        var (handler, context, receipts, reference) = Fixture("{}", false);
        receipts.Setup(store => store.GetAsync(reference.OperationId!, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        Func<Task> action = () => handler.HandleAsync(context);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Reference_is_stable_for_retry_and_distinct_for_another_run()
    {
        var (handler, context, _, reference) = Fixture("{}", false);
        handler.CreateReference(context, reference.IntentHash).Should().Be(reference);
        var another = new ActionNodeExecutionContext(Guid.NewGuid(), context.NodeId, context.ActionDefinition, context.ActingUserId);
        handler.CreateReference(another, reference.IntentHash).OperationId.Should().NotBe(reference.OperationId);
    }

    private static (EmitNodeHandler Handler, ActionNodeExecutionContext Context, Mock<IActionEmitStore> Receipts,
        ActionExternalOperationReference Reference) Fixture(string payload, bool tenant, bool admitted = true)
    {
        var node = new ActionNode { Id = Guid.NewGuid(), NodeType = ActionNodeType.Emit, Config = "{\"Payload\":" + payload + "}" };
        var graph = new ActionDefinition { Id = Guid.NewGuid(), Nodes = [node] };
        var context = new ActionNodeExecutionContext(Guid.NewGuid(), node.Id, graph, Guid.NewGuid(),
            actingTenantId: tenant ? Guid.NewGuid() : null);
        using var config = JsonDocument.Parse(node.Config);
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new ActionFrozenNodeInput(1,
            context.RunId, context.NodeId, ActionNodeType.Emit, context.ActingUserId, context.ActingTenantId,
            config.RootElement.Clone(), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>())));
        var reference = ActionEmitService.Reference(context, CanonicalJson.HashCanonical(frozen));
        var executions = new Mock<IActionNodeExecutionStore>();
        executions.Setup(store => store.GetByRunAndNodeAsync(context.RunId, context.NodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(admitted ? AZOAResult<ActionNodeExecution>.Success(new() { ExternalOperation = reference, FrozenInput = frozen })
                : AZOAResult<ActionNodeExecution>.Failure("Not admitted."));
        var receipts = new Mock<IActionEmitStore>();
        var service = new ActionEmitService(receipts.Object, Mock.Of<IActionRunStore>(), executions.Object,
            new SigningAdmissionAuthorityFactory(Mock.Of<IUserStore>(), Mock.Of<IWalletStore>(), Mock.Of<ISurrealExecutor>()));
        return (new(service), context, receipts, reference);
    }
}
