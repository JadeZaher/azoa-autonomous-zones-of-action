using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IBlockchainOperationManager
{
    /// <summary>Executes an explicit caller operation; native calls require DeploymentRequest and a stable request key. Action effects use frozen domain admission.</summary>
    Task<AZOAResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, AZOARequest? request = null);
    Task<AZOAResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, AZOARequest? request = null);
    /// <summary>Reads an operation with optional trusted-internal ownership scope; native states come from their durable journal.</summary>
    Task<AZOAResult<IBlockchainOperation>> GetAsync(Guid id, Guid? userId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(Guid userId, AZOARequest? request = null);
}
