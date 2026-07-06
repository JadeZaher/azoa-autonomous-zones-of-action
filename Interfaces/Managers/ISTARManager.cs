using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Ecosystem;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Discriminator the controller uses to translate a manager auth failure
/// into the right HTTP status without string-matching <c>Message</c>.
/// Carried via <see cref="AZOAResult{T}.Message"/> prefix.
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
    Task<AZOAResult<ISTARODK>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<ISTARODK>>> GetAllAsync(AZOARequest? request = null);

    /// <summary>
    /// Creates a new STARODK or updates an existing one, scoped to the
    /// authenticated <paramref name="avatarId"/>. Closes IDORs in two ways:
    ///   1. POST (routeId == null): existing-record lookup is by
    ///      (name, avatarId) — name collisions across avatars never overwrite.
    ///   2. PUT (routeId != null): lookup is by id, and the loaded record's
    ///      AvatarId MUST equal <paramref name="avatarId"/> or the operation
    ///      fails with <see cref="STARODKAuthorizationError.Forbidden"/>.
    /// </summary>
    Task<AZOAResult<ISTARODK>> CreateOrUpdateAsync(
        STARODKCreateModel model,
        Guid avatarId,
        Guid? routeId = null,
        AZOARequest? request = null);

    // avatarId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null avatarId is the trusted
    // internal path (quest node handlers) which has no avatar context and runs
    // unscoped — never pass null from a controller.
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? avatarId = null, AZOARequest? request = null);
    Task<AZOAResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null);
    Task<AZOAResult<ISTARODK>> DeployAsync(Guid id, Guid? avatarId = null, AZOARequest? providerRequest = null);

    // ── Ecosystem tree (D2) ─────────────────────────────────────────────────
    //
    // A STARODK owns an ecosystem: a TREE of EcosystemNodes, each attaching a
    // DappSeries (or a nested STARODK) as a composable dApp. AddDappSeriesAsync
    // attaches a node (lazily creating the ecosystem on first attach) and
    // re-walks the tree to regenerate the owning STARODK's composed multi-dApp
    // GeneratedCode. avatarId is authoritative and IDOR-scoped: a non-null value
    // enforces the ownership guard (never trust a caller-supplied owner id).

    /// <summary>
    /// Attaches a DappSeries (or nested STARODK) as a node in the STARODK's
    /// ecosystem tree, then re-walks the tree to regenerate the composed
    /// multi-dApp <see cref="ISTARODK.GeneratedCode"/>. Guards against cycles in
    /// the parent chain (holon parent-cycle precedent). Ownership is scoped to
    /// <paramref name="avatarId"/>.
    /// </summary>
    Task<AZOAResult<EcosystemTree>> AddDappSeriesAsync(Guid starOdkId, AddDappSeriesRequest request, Guid? avatarId = null, AZOARequest? providerRequest = null);

    /// <summary>Returns the STARODK's ecosystem assembled into a parent/children tree.</summary>
    Task<AZOAResult<EcosystemTree>> GetEcosystemAsync(Guid starOdkId, Guid? avatarId = null, AZOARequest? providerRequest = null);
}
