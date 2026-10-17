using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>
/// Round-4 resource IDOR (actions M1–M5 + dependency-status leak): action routes driven
/// through <see cref="ActionController"/> over the real <see cref="ActionManager"/> and
/// in-memory stores. Each test shows the other user denied and the owner allowed.
/// </summary>
public class ActionIdorFlowTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();
    private static readonly Guid ThirdParty = Guid.NewGuid();

    private readonly InMemoryActionStore _actions = new();
    private readonly InMemoryActionRunStore _runs = new();
    private readonly InMemoryActionNodeExecutionStore _executions = new();

    private ActionController ControllerFor(Guid userId)
    {
        var manager = new ActionManager(
            _actions, _runs, _executions,
            new ActionDagValidator(), new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            workflowAdmission: new InMemoryActionWorkflowAdmissionStore(_runs, _executions, new InMemorySagaStore()));
        return new ActionController(manager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
                }
            }
        };
    }

    private ActionDefinition SeedAction(Guid owner, bool isPublic)
    {
        var entry = new ActionNode { Id = Guid.NewGuid(), Name = "only", IsEntry = true, IsTerminal = true, NodeType = ActionNodeType.Condition, Config = "{}" };
        var action = new ActionDefinition { Id = Guid.NewGuid(), Name = "a", UserId = owner, IsPublic = isPublic, Nodes = [entry] };
        entry.ActionId = action.Id;
        _actions.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action;
    }

    private async Task<ActionRun> SeedRunAsync(Guid actionId, Guid user, ActionRunStatus status)
    {
        var run = new ActionRun { Id = Guid.NewGuid(), ActionId = actionId, UserId = user, Status = status, StartedAt = DateTime.UtcNow };
        await _runs.CreateAsync(run);
        return run;
    }

    private static T Payload<T>(ActionResult<T> result) where T : class =>
        (result.Result as ObjectResult)?.Value as T ?? throw new InvalidOperationException("No payload.");

    [Fact]
    public async Task AnotherUsersRun_IsIndistinguishableFromAMissingRun_OnEveryRunRoute()
    {
        var action = SeedAction(Owner, isPublic: true);
        var run = await SeedRunAsync(action.Id, Owner, ActionRunStatus.AwaitingReconciliation);
        var stranger = ControllerFor(Stranger);
        var missing = Payload(await stranger.GetRun(Guid.NewGuid(), null)).Message;

        Payload(await stranger.GetRun(run.Id, null)).Message.Should().Be(missing);
        Payload(await stranger.GetExecutionState(run.Id, null)).Message.Should().Be(missing);
        Payload(await stranger.Reconcile(run.Id, null)).Message.Should().Be(missing);
        Payload(await stranger.Signal(run.Id, new ActionSignalRequest { GateId = "g" }, null)).Message.Should().Be(missing);
        Payload(await stranger.MarkRunFailed(run.Id, new ActionMarkFailedRequest { Reason = "x" }, null)).Message.Should().Be(missing);
        (await _runs.GetByIdAsync(run.Id)).Result!.Status.Should().Be(ActionRunStatus.AwaitingReconciliation);

        (await ControllerFor(Owner).GetRun(run.Id, null)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task ValidateRoute_OnAnotherUsersPrivateAction_IsRefusedAsNotFound_AndTheOwnerValidates()
    {
        var action = SeedAction(Owner, isPublic: false);

        var probe = await ControllerFor(Stranger).Validate(action.Id, null);
        var missing = await ControllerFor(Stranger).Validate(Guid.NewGuid(), null);

        Payload(probe).Message.Should().Be("Action not found.").And.Be(Payload(missing).Message);
        (await ControllerFor(Owner).Validate(action.Id, null)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Templates_PrivateOnesStayWithTheirAuthor_AndAPublicTemplateCannotLiftAThirdPartysPrivateNodeTemplate()
    {
        var thirdParty = ControllerFor(ThirdParty);
        var secretNode = Payload(await thirdParty.CreateNodeTemplate(new ActionNodeTemplateCreateModel
        {
            Name = "secret", NodeType = ActionNodeType.Condition, DefaultConfig = "{\"apiKey\":\"third-party-secret\"}", IsPublic = false,
        }, null)).Result!;
        var owner = ControllerFor(Owner);
        var privateTemplate = Payload(await owner.CreateTemplate(new ActionTemplateCreateModel { Name = "mine", IsPublic = false }, null)).Result!;
        var publicTemplate = Payload(await owner.CreateTemplate(new ActionTemplateCreateModel
        {
            Name = "lifter", IsPublic = true,
            Nodes = [new ActionNodeCreateModel { Name = "n", NodeTemplateId = secretNode.Id, Config = "{}", IsEntry = true, IsTerminal = true }],
        }, null)).Result!;
        var stranger = ControllerFor(Stranger);

        (await stranger.GetTemplate(privateTemplate.Id, null)).Result.Should().BeOfType<NotFoundObjectResult>();
        Payload(await stranger.ListTemplates(null)).Result!.Select(t => t.Id).Should().Equal(publicTemplate.Id);
        Payload(await stranger.ListNodeTemplates(null)).Result.Should().BeEmpty();
        Payload(await stranger.InstantiateTemplate(privateTemplate.Id, null, null)).Message.Should().Be("Action template not found.");

        var instantiated = Payload(await stranger.InstantiateTemplate(publicTemplate.Id, null, null)).Result!;
        instantiated.Nodes.Should().ContainSingle().Which.Config.Should().NotContain("third-party-secret");

        (await owner.GetTemplate(privateTemplate.Id, null)).Result.Should().BeOfType<OkObjectResult>();
        Payload(await thirdParty.ListNodeTemplates(null)).Result!.Select(t => t.Id).Should().Equal(secretNode.Id);
    }

    [Fact]
    public async Task Dependencies_CannotProbeAPrivateAction_AndAreSatisfiedOnlyByTheRunnersOrOwnersRuns()
    {
        var privateTarget = SeedAction(Owner, isPublic: false);
        var publicTarget = SeedAction(Owner, isPublic: true);
        var mine = SeedAction(Stranger, isPublic: false);
        var stranger = ControllerFor(Stranger);

        var probe = await stranger.AddDependency(mine.Id, new ActionDependencyCreateModel { DependsOnActionId = privateTarget.Id }, null);
        Payload(probe).Message.Should().Be("Depended-on action not found.");

        var allowed = await stranger.AddDependency(mine.Id, new ActionDependencyCreateModel { DependsOnActionId = publicTarget.Id }, null);
        allowed.Result.Should().BeOfType<OkObjectResult>();

        await SeedRunAsync(publicTarget.Id, ThirdParty, ActionRunStatus.Succeeded);
        Payload(await stranger.GetDependencyStatus(mine.Id, null)).Result!.AllSatisfied
            .Should().BeFalse("another user's success says nothing to this author and must not leak through the status");

        await SeedRunAsync(publicTarget.Id, Stranger, ActionRunStatus.Succeeded);
        Payload(await stranger.GetDependencyStatus(mine.Id, null)).Result!.AllSatisfied.Should().BeTrue();
    }
}
