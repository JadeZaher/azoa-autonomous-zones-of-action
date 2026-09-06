// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Public aggregate view of an action run and its sanitized node executions.</summary>
public sealed class ActionExecutionStateResponse
{
    public Guid RunId { get; init; }
    public Guid ActionId { get; init; }
    public ActionRunStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public int TotalNodes { get; init; }
    public int CompletedNodes { get; init; }
    public int FailedNodes { get; init; }
    public int PendingNodes { get; init; }
    public IEnumerable<ActionNodeExecutionResponse> NodeExecutions { get; init; } =
        Array.Empty<ActionNodeExecutionResponse>();
}
