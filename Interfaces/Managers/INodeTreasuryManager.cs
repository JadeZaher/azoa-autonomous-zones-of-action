using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INodeTreasuryManager
{
    /// <summary>Loads and provider-validates one canonical chain/network destination.</summary>
    Task<AZOAResult<NodeTreasuryDestinationResponse>> GetDestinationAsync(
        string chain,
        ChainNetwork network,
        CancellationToken ct = default);

    /// <summary>Validates and atomically versions a destination plus its audit row.</summary>
    /// <param name="request">Destination values and optional expected version.</param>
    /// <param name="actorAvatarId">Authenticated node-governance actor.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AZOAResult<NodeTreasuryDestinationResponse>> UpdateDestinationAsync(
        NodeTreasuryDestinationUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default);

    /// <summary>Loads the newest operator-only treasury audit rows.</summary>
    Task<AZOAResult<IEnumerable<NodeTreasuryAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default);
}
