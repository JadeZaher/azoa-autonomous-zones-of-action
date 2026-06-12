using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

/// <summary>
/// Discriminator the controller uses to translate a manager auth failure
/// into the right HTTP status without string-matching <c>Message</c>.
/// Carried via <see cref="OASISResult{T}.Message"/> prefix.
/// </summary>
public static class STARODKAuthorizationError
{
    /// <summary>Record exists but is owned by a different avatar — surfaces as 403.</summary>
    public const string Forbidden = "STARODK_FORBIDDEN: ";

    /// <summary>Record id from the route did not match any record — surfaces as 404.</summary>
    public const string NotFound  = "STARODK_NOT_FOUND: ";
}

public interface ISTARManager
{
    Task<OASISResult<ISTARODK>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<ISTARODK>>> GetAllAsync(OASISRequest? request = null);

    /// <summary>
    /// Creates a new STARODK or updates an existing one, scoped to the
    /// authenticated <paramref name="avatarId"/>. Closes IDORs in two ways:
    ///   1. POST (routeId == null): existing-record lookup is by
    ///      (name, avatarId) — name collisions across avatars never overwrite.
    ///   2. PUT (routeId != null): lookup is by id, and the loaded record's
    ///      AvatarId MUST equal <paramref name="avatarId"/> or the operation
    ///      fails with <see cref="STARODKAuthorizationError.Forbidden"/>.
    /// </summary>
    Task<OASISResult<ISTARODK>> CreateOrUpdateAsync(
        STARODKCreateModel model,
        Guid avatarId,
        Guid? routeId = null,
        OASISRequest? request = null);

    // avatarId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null avatarId is the trusted
    // internal path (quest node handlers) which has no avatar context and runs
    // unscoped — never pass null from a controller.
    Task<OASISResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, OASISRequest? request = null);
    Task<OASISResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, Guid? avatarId = null, OASISRequest? providerRequest = null);
    Task<OASISResult<ISTARODK>> DeployAsync(Guid id, Guid? avatarId = null, OASISRequest? providerRequest = null);
}
