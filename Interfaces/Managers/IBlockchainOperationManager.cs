using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IBlockchainOperationManager
{
    Task<AZOAResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, AZOARequest? request = null);
    Task<AZOAResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, AZOARequest? request = null);
    // avatarId scopes the read to the owner: a non-null value enforces the
    // ownership check (closes the IDOR on the HTTP route). A null avatarId is
    // the trusted internal path (quest BlockchainExecute node handler) which
    // has no avatar context — never pass null from a controller.
    Task<AZOAResult<IBlockchainOperation>> GetAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, AZOARequest? request = null);
}
