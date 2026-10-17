using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionWorkflowRequestIdentityTests
{
    [Fact]
    public async Task ExactRequestReplaysBeforeMutableSourceChecksAndChangedConsentConflicts()
    {
        var actionId = Guid.NewGuid(); var userId = Guid.NewGuid(); Guid? actingTenantId = Guid.NewGuid();
        const string key = "saved-browser-request";
        const bool acknowledgeEconomicEffects = true;
        var request = new AZOARequest { IdempotencyKey = key, ExpectedPublishedHash = "reviewed-publication" };
        var run = new ActionRun { Id = ActionWorkflowIdentity.RunId(actionId, userId, actingTenantId, key),
            ActionId = actionId, UserId = userId, ActingTenantId = actingTenantId, Status = ActionRunStatus.Succeeded,
            InvocationIdentity = CanonicalJson.Hash(new { actionId, userId, actingTenantId, key, acknowledgeEconomicEffects,
                request.ExpectedPublishedHash }) };
        var runs = new InMemoryActionRunStore();
        await runs.CreateAsync(run);
        var sources = new Mock<IActionStore>(MockBehavior.Strict);
        var harness = new DurableActionExecutionHarness(sources.Object, runs, new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(), new ActionDagExecutabilityValidator(), new ActionNodeHandlerRegistry([]),
            new InMemorySagaStore(), WalletManagerMocks.Empty(), BlockchainProviderFactoryFakes.Returning(), BindingResolverFakes.PassThrough());

        (await harness.Manager.StartWorkflowRunAsync(actionId, userId, request, actingTenantId, true)).Result!.Id.Should().Be(run.Id);
        (await harness.Manager.StartWorkflowRunAsync(actionId, userId, request, actingTenantId, false)).IsError.Should().BeTrue();
        (await harness.Manager.StartWorkflowRunAsync(actionId, userId)).IsError.Should().BeTrue();
        (await harness.Manager.TryReplayAsync("deployment", "request", null!, userId, null, default, false))!.IsError.Should().BeTrue();
        sources.VerifyNoOtherCalls();
    }
}
