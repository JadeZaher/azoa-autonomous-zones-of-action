using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IAvatarManager
{
    Task<AZOAResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, AZOARequest? request = null);
    Task<AZOAResult<string>> LoginAsync(AvatarLoginModel model, AZOARequest? request = null);
    Task<AZOAResult<IAvatar>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IAvatar>>> GetAllAsync(AZOARequest? request = null);
    Task<AZOAResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null);
}
