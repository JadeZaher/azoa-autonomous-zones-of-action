using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// The pre-execution chain-capability gate (economic-primitive-nodes D1).
///
/// <para>A node handler that declares
/// <see cref="AZOA.WebAPI.Interfaces.Quest.IQuestNodeHandler.RequiresChainCapability"/>
/// <c>== true</c> (the Tier-2 chain actions: Swap/Grant/Transfer/Refund) may only
/// run when the run's actor avatar has a wallet bound. Both dispatch seams — the
/// legacy synchronous <c>QuestManager</c> executor and the durable
/// <c>QuestNodeStepHandler</c> saga step — call this single helper BEFORE invoking
/// <c>HandleAsync</c>, so the rule cannot be bypassed via the durable path.</para>
///
/// <para><b>Fails closed.</b> "Has a wallet bound" is resolved from the actor
/// avatar (D2 — no <c>QuestRun.BoundWalletId</c> field is introduced) via
/// <see cref="IWalletManager.QueryAsync"/>. A query error is treated as
/// "no wallet bound" so a transient lookup failure never lets a chain action
/// broadcast.</para>
/// </summary>
public static class ChainCapabilityGate
{
    /// <summary>
    /// The exact failure message the engine records when a chain-requiring node is
    /// rejected pre-execution. Stable so callers and tests share one string.
    /// </summary>
    public const string NoWalletBoundMessage = "chain capability required: no wallet bound to run";

    /// <summary>
    /// Returns <c>true</c> when the <paramref name="avatarId"/> actor has at least
    /// one wallet bound (a usable chain capability). Resolves from the actor via
    /// <see cref="IWalletManager.QueryAsync"/> with an unfiltered
    /// <see cref="WalletQueryRequest"/> (all of the avatar's wallets). A query
    /// error or an empty result is "no wallet bound" — fails closed.
    /// </summary>
    public static async Task<bool> HasWalletBoundAsync(
        IWalletManager walletManager, Guid avatarId, CancellationToken ct = default)
    {
        // QueryAsync scopes by the authenticated avatar id argument; an empty
        // WalletQueryRequest applies no extra filter, so this lists ALL of the
        // actor's wallets. GetAsync would need a wallet id we do not have here.
        var result = await walletManager.QueryAsync(new WalletQueryRequest(), avatarId);

        if (result.IsError || result.Result is null)
            return false; // fail closed — a lookup failure is "no capability"

        return result.Result.Any();
    }
}
