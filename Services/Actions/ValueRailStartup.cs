// SPDX-License-Identifier: UNLICENSED
//
// OI-28. The account pause is only a control if something reads whether it is wired. This file
// is that reader. See Services/Actions/AGENTS.md §rail-capability.

using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Sagas;
using AZOA.WebAPI.Services.Actions.Workflow;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// A dispatch seam that can say whether the account pause is actually honoured on it.
/// </summary>
/// <remarks>
/// Both seams answer from the same place — the <see cref="ValueRailCapabilityGate"/> they each
/// construct — so the answer is the gate's own, never a second claim about it.
/// </remarks>
public interface IValueNodePauseSeam
{
    /// <summary>
    /// True when this seam holds the reversal ledger the account pause is read from. False means
    /// every value-moving node on this seam passes the pause unconditionally.
    /// </summary>
    bool IsAccountPauseEnforced { get; }
}

/// <summary>
/// Boot-time proof that the account pause is honoured at BOTH dispatch seams.
/// </summary>
/// <remarks>
/// <para><b>Why a boot guard and not a per-node refusal.</b> The reversal ledger is a trailing
/// OPTIONAL constructor parameter on <c>ActionManager</c> and <c>ActionNodeStepHandler</c> — it
/// has to be, because a few dozen unit fixtures construct them positionally. That optionality is
/// exactly the hole: a container composed without <c>AddFiatReversalPipeline</c> would silently
/// disable the pause on every value node, on both seams, with nothing saying so. Refusing every
/// node instead would turn a composition mistake into a runtime mystery; refusing to BOOT names
/// it at the moment it is made, and it is the same shape as
/// <c>FiatRailStartup.GuardLiveCapabilityAsync</c> and
/// <c>ExternalAuthRuntimeSafety.GuardRegistrationsResolve</c>.</para>
/// <para><b>It is the only production reader of
/// <see cref="ValueRailCapabilityGate.IsAccountPauseEnforced"/>.</b> Before it existed, that
/// property's only readers were its own tests — a positive signal nothing acted on.</para>
/// </remarks>
public static class ValueRailStartup
{
    /// <summary>
    /// Throws unless both dispatch seams resolve AND report the account pause enforced.
    /// </summary>
    /// <param name="services">The built root provider; a scope is created for the scoped seams.</param>
    public static void GuardAccountPauseWired(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Both seams are scoped. Resolving them off the root would either throw or pin a scoped
        // graph to the root for the life of the process.
        using var scope = services.GetService<IServiceScopeFactory>()?.CreateScope();
        var resolver = scope?.ServiceProvider ?? services;

        Require(Resolve<IActionManager>(resolver, "the in-process action executor"),
            "the in-process action executor (Managers/ActionManager.cs)");

        Require(
            Resolve<IStepHandler<ActionStepPayload>>(resolver, "the durable action step handler"),
            "the durable action step handler (Services/Actions/Workflow/ActionNodeStepHandler.cs)");
    }

    private static object Resolve<T>(IServiceProvider resolver, string description) where T : class
    {
        T? resolved;
        try
        {
            resolved = resolver.GetService<T>();
        }
        catch (Exception exception)
        {
            // Registered but unconstructable. Rethrown with the seam named, because the raw
            // container error names a leaf dependency and not the thing that will not run.
            throw new InvalidOperationException(
                $"{description} is registered but could not be constructed, so no value-moving "
                + "action node can run on it. Fix its composition.", exception);
        }

        if (resolved is null)
            throw new InvalidOperationException(
                $"{description} is not registered, so no value-moving action node can run and the "
                + "account pause cannot be honoured on that seam. Register it during composition.");

        return resolved;
    }

    private static void Require(object seam, string description)
    {
        if (seam is not IValueNodePauseSeam reporting)
            throw new InvalidOperationException(
                $"{description} does not report whether the account pause is enforced, so this node "
                + $"cannot prove it honours a reversal risk flag. Implement {nameof(IValueNodePauseSeam)} "
                + "on it.");

        if (!reporting.IsAccountPauseEnforced)
            throw new InvalidOperationException(
                $"{description} was composed WITHOUT the reversal ledger, so every value-moving "
                + "action node on that seam would pass the account pause unconditionally: an account "
                + "with an unresolved reversal could move the disputed units through the workflow "
                + "engine. Call ReversalRegistration.AddFiatReversalPipeline during composition.");
    }
}
