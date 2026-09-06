using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IHolonManager
{
    // callerUserId scopes READS to the owner-or-public policy: a holon is returned
    // iff h.UserId == callerUserId || h.IsPublic. A null callerUserId fails
    // closed (public-only) — pass the authenticated user from the controller, or the
    // acting user from a action node handler. Single-get of a non-owned/non-private
    // holon returns a "not found"-style result (existence is NOT confirmed).
    // See Controllers/AGENTS.md §cross-tenant-read-scope.
    Task<AZOAResult<IHolon>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetAllAsync(Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IHolon>> CreateAsync(HolonCreateModel model, Guid userId, AZOARequest? request = null);
    // userId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null userId is the trusted
    // internal path (action node handlers) which has no user context and runs
    // unscoped — never pass null from a controller.
    Task<AZOAResult<IHolon>> UpdateAsync(Guid id, HolonUpdateModel model, Guid? userId = null, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? userId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> QueryAsync(HolonQueryRequest query, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IHolon>> InteractAsync(Guid id, HolonInteractionRequest request, Guid? userId = null, AZOARequest? providerRequest = null);

    // Holarchy traversal — reads are owner-or-public scoped by callerUserId (see GetAsync).
    Task<AZOAResult<IEnumerable<IHolon>>> GetChildrenAsync(Guid parentId, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetPeersAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetAncestorsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolon>>> GetDescendantsAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);

    // Holonic functionality — operations across the holarchy
    Task<AZOAResult<int>> PropagateAsync(Guid id, HolonPropagateRequest request, Guid? userId = null, AZOARequest? providerRequest = null);
    Task<AZOAResult<HolonComposition>> ComposeAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IHolon>> CloneAsync(Guid id, HolonCloneRequest request, Guid userId, AZOARequest? providerRequest = null);
    Task<AZOAResult<bool>> MoveSubtreeAsync(Guid id, Guid newParentId, Guid? userId = null, AZOARequest? request = null);
}
