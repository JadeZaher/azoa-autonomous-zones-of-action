using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Interfaces.ActionExecution;

/// <summary>
/// Executes one <see cref="ActionNodeType"/>; exactly one handler per type.
/// </summary>
/// <remarks>
/// <para>
/// Contract migration: previously took <c>(ActionDefinition, ActionNode)</c> and mutated
/// <c>ActionNode.State/Output/Error</c> in place. After the
/// <c>action-temporal-fork-model</c> track the handler is run-aware via
/// <see cref="ActionNodeExecutionContext"/>; runtime state is written by
/// <c>ActionManager</c> onto the per-(run, node) <see cref="ActionNodeExecution"/>
/// row. Handlers no longer mutate the action <i>definition</i>.
/// </para>
/// </remarks>
public interface IActionNodeHandler
{
    /// <summary>The single node type this handler dispatches.</summary>
    ActionNodeType NodeType { get; }

    /// <summary>
    /// True if this node moves value and therefore needs a settlement capability on
    /// the rail it lands on. The engine refuses to run such a node pre-execution when
    /// the actor has no usable destination there (fails closed). Default false: holon
    /// transforms are pure metadata and settle nothing. Only the Tier-2
    /// value-action subset (Swap/Grant/Transfer/Refund/Bridge/Back/FungibleTokenCreate)
    /// overrides this to true.
    /// </summary>
    bool RequiresChainCapability => false;

    /// <summary>
    /// The value rail this node will settle on, read from its DECLARED config — e.g. a
    /// Bridge node settles on its source chain, a FungibleTokenCreate on its configured
    /// chain. Null means "whatever the platform default rail is", which is the right
    /// answer for a node whose config names no rail.
    /// <para>Read at the capability gate, which asks whether the actor can receive value
    /// THERE rather than anywhere. See <c>Services/Actions/AGENTS.md</c> §rail-capability.</para>
    /// </summary>
    string? ResolveSettlementRailId(ActionNode node) => null;

    /// <summary>
    /// Executes the node identified by <paramref name="context"/> and returns
    /// either a success <see cref="ActionNodeHandlerResult"/> carrying serialized
    /// output JSON, or a failure carrying the error message. The manager turns
    /// the result into a <see cref="ActionNodeExecution"/> transition.
    /// </summary>
    Task<ActionNodeHandlerResult> HandleAsync(
        ActionNodeExecutionContext context,
        CancellationToken ct = default);
}
