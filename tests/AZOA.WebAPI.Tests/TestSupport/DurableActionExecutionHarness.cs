using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Workflow;
using AZOA.WebAPI.Services.Signing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AZOA.WebAPI.Tests.TestSupport;

public sealed class DurableActionExecutionHarness
{
    public ActionManager Manager { get; }
    private readonly IActionRunStore _runs;
    private readonly ActionNodeStepHandler _stepper;
    private readonly Admission _admission;

    public DurableActionExecutionHarness(IActionStore actions, IActionRunStore runs, IActionNodeExecutionStore executions,
        IActionDagValidator validator, IActionDagExecutabilityValidator executable, IActionNodeHandlerRegistry handlers,
        ISagaStore sagas, IWalletManager wallets, IBlockchainProviderFactory chains, ActionConfigBindingResolver bindings,
        IActionAccessRequestStore? accessRequests = null)
    {
        _runs = runs;
        _admission = new(runs, executions);
        Manager = new(actions, runs, executions, validator, executable, handlers, sagas, wallets, chains, bindings,
            accessRequestStore: accessRequests, workflowAdmission: _admission);
        _stepper = new(actions, runs, executions, handlers, sagas, wallets, chains, bindings,
            NullLogger<ActionNodeStepHandler>.Instance, workflowAdmission: _admission,
            signingAuthority: OwnerSigningAuthority());
    }

    public async Task<AZOAResult<ActionRun>> ExecuteAsync(Guid action, Guid actor)
    {
        var started = await Manager.ExecuteAsync(action, actor, Request(actor));
        if (started.IsError || started.Result is null) return started;
        for (var count = 0; _admission.Pending.Count > 0; count++)
        {
            if (count > 100) throw new InvalidOperationException("Workflow fixture did not converge.");
            var node = _admission.Pending.Dequeue();
            var result = await _stepper.ExecuteAsync(new StepExecutionContext<ActionStepPayload>(ActionWorkflowSaga.Name,
                node.ToString("N"), started.Result.Id.ToString("N"), "fixture-step", 1,
                new(started.Result.Id, action, actor, node)), default);
            if (result.Park is not null) throw new InvalidOperationException($"This immediate DAG fixture unexpectedly parked: {result.Park} {result.Error}");
        }
        return await _runs.GetByIdAsync(started.Result.Id);
    }

    /// <summary>Signing authority for first-party owner credentials (no tenant, so no grant reads reach the executor).</summary>
    public static SigningAdmissionAuthorityFactory OwnerSigningAuthority() => new(Moq.Mock.Of<IUserStore>(), Moq.Mock.Of<IWalletStore>(),
        Moq.Mock.Of<SurrealForge.Client.Query.ISurrealExecutor>());

    /// <summary>A first-party login request for <paramref name="actor"/>: a fresh run key plus the owner credential the durable stepper binds.</summary>
    public static AZOARequest Request(Guid actor) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        AuthenticationWitness = new(1, "Jwt", actor, null, null, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(1),
            null, null, [], true, TokenUse: AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin),
    };

    private sealed class Admission(IActionRunStore runs, IActionNodeExecutionStore executions) : IActionWorkflowAdmissionStore
    {
        public Queue<Guid> Pending { get; } = new();
        private readonly HashSet<(Guid Run, Guid Node)> _seen = [];

        public async Task<AZOAResult<ActionRun>> AdmitAsync(ActionWorkflowAdmission admission, CancellationToken ct = default)
        {
            var created = await runs.CreateAsync(admission.Run, ct);
            foreach (var node in admission.Graph.Nodes)
                await executions.CreateAsync(new ActionNodeExecution { Id = Guid.NewGuid(), RunId = admission.Run.Id, NodeId = node.Id }, ct);
            await EnqueueNodeAsync(admission.Run.Id, admission.EntryNodeId, "", ct);
            return created;
        }

        public Task EnqueueNodeAsync(Guid runId, Guid nodeId, string payload, CancellationToken ct = default)
        {
            if (_seen.Add((runId, nodeId))) Pending.Enqueue(nodeId);
            return Task.CompletedTask;
        }
    }
}
