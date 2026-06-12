using OASIS.WebAPI.Core;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IBlockchainOperationManager
{
    Task<OASISResult<IBlockchainOperation>> ExecuteAsync(IBlockchainOperation operation, OASISRequest? request = null);
    Task<OASISResult<IBlockchainOperation>> BuildAndExecuteAsync(Func<BlockchainOperationBuilder, IBlockchainOperation> build, OASISRequest? request = null);
    // avatarId scopes the read to the owner: a non-null value enforces the
    // ownership check (closes the IDOR on the HTTP route). A null avatarId is
    // the trusted internal path (quest BlockchainExecute node handler) which
    // has no avatar context — never pass null from a controller.
    Task<OASISResult<IBlockchainOperation>> GetAsync(Guid id, Guid? avatarId = null, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IBlockchainOperation>>> GetByAvatarAsync(Guid avatarId, OASISRequest? request = null);
}
