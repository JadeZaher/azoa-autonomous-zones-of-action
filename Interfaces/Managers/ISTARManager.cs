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
    /// <summary>Record exists but is owned by a different user — surfaces as 403.</summary>
    public const string Forbidden = "STARODK_FORBIDDEN: ";

    /// <summary>Record id from the route did not match any record — surfaces as 404.</summary>
    public const string NotFound  = "STARODK_NOT_FOUND: ";
}

public interface ISTARManager
{
    // callerUserId scopes READS to owner-or-public: a record is returned iff
    // record.UserId == callerUserId || record.IsPublic. Null fails closed
    // (public-only). Single-get of a non-owned/non-private record returns a
    // "not found"-style result. See Controllers/AGENTS.md §cross-tenant-read-scope.
    Task<AZOAResult<ISTARODK>> GetAsync(Guid id, Guid? callerUserId = null, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<ISTARODK>>> GetAllAsync(Guid? callerUserId = null, AZOARequest? request = null);

    /// <summary>
    /// Creates a new STARODK or updates an existing one, scoped to the
    /// authenticated <paramref name="userId"/>. Closes IDORs in two ways:
    ///   1. POST (routeId == null): existing-record lookup is by
    ///      (name, userId) — name collisions across users never overwrite.
    ///   2. PUT (routeId != null): lookup is by id, and the loaded record's
    ///      UserId MUST equal <paramref name="userId"/> or the operation
    ///      fails with <see cref="STARODKAuthorizationError.Forbidden"/>.
    /// </summary>
    Task<AZOAResult<ISTARODK>> CreateOrUpdateAsync(
        STARODKCreateModel model,
        Guid userId,
        Guid? routeId = null,
        AZOARequest? request = null);

    // userId scopes ownership: a non-null value enforces the IsOwnedBy guard
    // (closes the IDOR for the HTTP routes). A null userId is the trusted
    // internal path (action node handlers) which has no user context and runs
    // unscoped — never pass null from a controller.
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid? userId = null, AZOARequest? request = null);
    Task<AZOAResult<ISTARODK>> GenerateAsync(Guid id, STARDappGenerationRequest request, Guid? userId = null, AZOARequest? providerRequest = null);
    Task<AZOAResult<ISTARODK>> DeployAsync(Guid id, Guid? userId = null, AZOARequest? providerRequest = null);

    // ── Ecosystem tree (D2) ─────────────────────────────────────────────────
    //
    // A STARODK owns an ecosystem: a TREE of EcosystemNodes, each attaching a
    // DappSeries (or a nested STARODK) as a composable dApp. AddDappSeriesAsync
    // attaches a node (lazily creating the ecosystem on first attach) and
    // re-walks the tree to regenerate the owning STARODK's composed multi-dApp
    // GeneratedCode. userId is authoritative and IDOR-scoped: a non-null value
    // enforces the ownership guard (never trust a caller-supplied owner id).

    /// <summary>
    /// Attaches a DappSeries (or nested STARODK) as a node in the STARODK's
    /// ecosystem tree, then re-walks the tree to regenerate the composed
    /// multi-dApp <see cref="ISTARODK.GeneratedCode"/>. Guards against cycles in
    /// the parent chain (holon parent-cycle precedent). Ownership is scoped to
    /// <paramref name="userId"/>.
    /// </summary>
    Task<AZOAResult<EcosystemTree>> AddDappSeriesAsync(Guid starOdkId, AddDappSeriesRequest request, Guid? userId = null, AZOARequest? providerRequest = null);

    /// <summary>Returns the STARODK's ecosystem assembled into a parent/children tree.</summary>
    Task<AZOAResult<EcosystemTree>> GetEcosystemAsync(Guid starOdkId, Guid? userId = null, AZOARequest? providerRequest = null);
}
