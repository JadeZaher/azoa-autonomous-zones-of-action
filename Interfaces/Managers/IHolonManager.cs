using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IHolonManager
{
    Task<OASISResult<IHolon>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetAllAsync(OASISRequest? request = null);
    Task<OASISResult<IHolon>> CreateAsync(HolonCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, OASISRequest? request = null);
    Task<OASISResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, OASISRequest? providerRequest = null);

    // Holarchy traversal — expose the holonic structure
    Task<OASISResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, OASISRequest? request = null);

    // Holonic functionality — operations across the holarchy
    Task<OASISResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, OASISRequest? providerRequest = null);
    Task<OASISResult<HolonComposition>> ComposeAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid avatarId, OASISRequest? providerRequest = null);
    Task<OASISResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, OASISRequest? request = null);
}
