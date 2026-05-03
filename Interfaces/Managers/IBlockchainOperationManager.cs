using OASIS.WebAPI.Core;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IBlockchainOperationManager
{
    Task<OASISResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, OASISRequest? request = null);
    Task<OASISResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, OASISRequest? request = null);
    Task<OASISResult<IBlockchainOperation>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null);
}
