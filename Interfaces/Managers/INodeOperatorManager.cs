using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>Dedicated authentication and control-plane boundary for the node operator.</summary>
public interface INodeOperatorManager
{
    Task<AZOAResult<NodeOperatorSessionResponse>> LoginAsync(
        NodeOperatorLoginRequest request,
        string clientAddress,
        CancellationToken ct = default);

    Task<AZOAResult<bool>> RevokeAllSessionsAsync(CancellationToken ct = default);
}
