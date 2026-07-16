using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INodeGovernanceManager
{
    /// <summary>Returns persisted governance parameters or configured defaults at version zero.</summary>
    Task<AZOAResult<NodeGovernanceParametersResponse>> GetParametersAsync(CancellationToken ct = default);

    /// <summary>Validates and applies an expected-version governance update.</summary>
    Task<AZOAResult<NodeGovernanceParametersResponse>> UpdateParametersAsync(
        NodeGovernanceParametersUpdateRequest request,
        Guid actorAvatarId,
        CancellationToken ct = default);

    /// <summary>Returns the operator-visible governance audit history.</summary>
    Task<AZOAResult<IEnumerable<NodeGovernanceAuditResponse>>> ListAuditAsync(
        int limit = 50,
        CancellationToken ct = default);
}
