using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IAvatarManager
{
    Task<OASISResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, OASISRequest? request = null);
    Task<OASISResult<string>> LoginAsync(AvatarLoginModel model, OASISRequest? request = null);
    Task<OASISResult<IAvatar>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IAvatar>>> GetAllAsync(OASISRequest? request = null);
    Task<OASISResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, Guid avatarId, OASISRequest? request = null);
}
