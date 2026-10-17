using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Queries contract return data without custody, nonce allocation or broadcast.</summary>
public interface IReadOnlyContractModule : IBlockchainProviderModule
{
    Task<AZOAResult<object>> QueryContractAsync(EvmReadCall request, CancellationToken ct = default);
}
