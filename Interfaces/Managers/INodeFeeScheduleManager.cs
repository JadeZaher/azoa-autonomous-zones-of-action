using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INodeFeeScheduleManager
{
    /// <summary>Loads the effective persisted or default local fee schedule.</summary>
    Task<AZOAResult<NodeFeeScheduleResponse>> GetScheduleAsync(CancellationToken ct = default);

    /// <summary>Validates and atomically versions a schedule plus its audit row.</summary>
    /// <param name="request">Partial schedule values and optional expected version.</param>
    /// <param name="actorAvatarId">Authenticated node-governance actor.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AZOAResult<NodeFeeScheduleResponse>> UpdateScheduleAsync(
        NodeFeeScheduleUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default);

    /// <summary>Loads the newest operator-only fee audit rows.</summary>
    Task<AZOAResult<IEnumerable<NodeFeeAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>Computes the version-pinned fee and net amount for a positive gross amount.</summary>
    Task<AZOAResult<NodeFeeQuoteResponse>> QuoteAsync(
        NodeFeeOperation operation,
        ulong grossAmount,
        CancellationToken ct = default);
}
