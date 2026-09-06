using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IBlockchainOperationManager
{
    Task<AZOAResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, AZOARequest? request = null);
    Task<AZOAResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, AZOARequest? request = null);
    // userId scopes the read to the owner: a non-null value enforces the
    // ownership check (closes the IDOR on the HTTP route). A null userId is
    // the trusted internal path (action BlockchainExecute node handler) which
    // has no user context — never pass null from a controller.
    Task<AZOAResult<IBlockchainOperation>> GetAsync(Guid id, Guid? userId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByUserAsync(Guid userId, AZOARequest? request = null);
}
