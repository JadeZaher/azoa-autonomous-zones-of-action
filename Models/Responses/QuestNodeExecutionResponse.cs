// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Public projection of a quest-node execution.</summary>
public sealed class QuestNodeExecutionResponse
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public Guid NodeId { get; init; }
    public QuestNodeState State { get; init; }

    /// <summary>Sanitized serialized node output, when the node succeeded.</summary>
    public string? Output { get; init; }

    public string? Error { get; init; }
    public string? TxHash { get; init; }
    public string? ChainType { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }

    /// <summary>Maps a durable execution using its already-sanitized output.</summary>
    public static QuestNodeExecutionResponse From(
        QuestNodeExecution execution,
        string? sanitizedOutput)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return new QuestNodeExecutionResponse
        {
            Id = execution.Id,
            RunId = execution.RunId,
            NodeId = execution.NodeId,
            State = execution.State,
            Output = sanitizedOutput,
            Error = execution.Error,
            TxHash = execution.TxHash,
            ChainType = execution.ChainType,
            StartedAt = execution.StartedAt,
            EndedAt = execution.EndedAt,
        };
    }
}
