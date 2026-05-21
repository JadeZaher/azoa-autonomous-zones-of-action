using OASIS.WebAPI.Models.Quest;

namespace OASIS.WebAPI.Services.Quest;

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
    public static QuestNodeHandlerResult Ok(string? outputJson, string? message = null)
        => QuestNodeHandlerResult.Ok(outputJson, message);

    /// <summary>
    /// Failure result carrying <paramref name="message"/>. The manager will
    /// write this to <see cref="QuestNodeExecution.Error"/> and transition
    /// the row to <see cref="QuestNodeState.Failed"/>.
    /// </summary>
    public static QuestNodeHandlerResult Fail(string message)
        => QuestNodeHandlerResult.Fail(message);
}
