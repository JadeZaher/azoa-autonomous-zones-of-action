// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Public aggregate view of a quest run and its sanitized node executions.</summary>
public sealed class QuestExecutionStateResponse
{
    public Guid RunId { get; init; }
    public Guid QuestId { get; init; }
    public QuestRunStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public int TotalNodes { get; init; }
    public int CompletedNodes { get; init; }
    public int FailedNodes { get; init; }
    public int PendingNodes { get; init; }
    public IEnumerable<QuestNodeExecutionResponse> NodeExecutions { get; init; } =
        Array.Empty<QuestNodeExecutionResponse>();
}
