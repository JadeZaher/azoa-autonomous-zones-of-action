using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Builds the success / failure <see cref="QuestNodeHandlerResult"/> for a
/// quest node handler. After the <c>quest-temporal-fork-model</c> track,
/// handlers no longer mutate <see cref="QuestNode"/> — runtime state lives on
/// <see cref="QuestNodeExecution"/>, written by the manager from the values
/// returned here.
/// </summary>
public static class QuestNodeResults
{
    /// <summary>
    /// Success result carrying serialized <paramref name="outputJson"/>. The
    /// manager will write this to <see cref="QuestNodeExecution.Output"/>
    /// and transition the row to <see cref="QuestNodeState.Succeeded"/>.
    /// </summary>
    public static QuestNodeHandlerResult Ok(
        string? outputJson, string? message = null, string? txHash = null, string? chainType = null)
        => QuestNodeHandlerResult.Ok(outputJson, message, txHash, chainType);

    /// <summary>
    /// Failure result carrying <paramref name="message"/>. The manager will
    /// write this to <see cref="QuestNodeExecution.Error"/> and transition
    /// the row to <see cref="QuestNodeState.Failed"/>.
    /// <para>
    /// A Tier-2 chain-action handler that broadcast a tx BEFORE failing (e.g. a
    /// confirmation-read timeout) MUST forward <paramref name="txHash"/>/
    /// <paramref name="chainType"/> so the reconcile-before-retry engine verifies
    /// chain truth instead of blind-retrying and double-minting
    /// (blockchain-recovery-and-portable-wallets §1.3). A non-chain failure omits
    /// both (the default) and behaves exactly as before.
    /// </para>
    /// </summary>
    public static QuestNodeHandlerResult Fail(string message, string? txHash = null, string? chainType = null)
        => QuestNodeHandlerResult.Fail(message, txHash, chainType);

    /// <summary>
    /// Invalid-config failure that can never succeed on retry (nothing was
    /// broadcast). The reconcile-before-retry engine fails such a node terminally
    /// without a chain probe or retry budget
    /// (blockchain-recovery-and-portable-wallets §1 invalid-mode handling).
    /// </summary>
    public static QuestNodeHandlerResult Invalid(string message)
        => QuestNodeHandlerResult.Invalid(message);
}
