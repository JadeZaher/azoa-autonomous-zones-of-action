using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Governance;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface INodeTransparencyManager
{
    /// <summary>Returns the node's current sanitized public economic policy snapshot.</summary>
    /// <param name="ct">Cancels the read without changing policy state.</param>
    /// <returns>A public projection with no actor ids, internal ids, or raw audit JSON.</returns>
    Task<AZOAResult<NodeTransparencySnapshotResponse>> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>Returns a bounded page of sanitized governance-policy history.</summary>
    /// <param name="limit">Requested page size; implementations clamp it to the public maximum.</param>
    /// <param name="cursor">Opaque cursor returned by the preceding page, or null for the newest page.</param>
    /// <param name="ct">Cancels the read without changing policy state.</param>
    /// <returns>A newest-first page and an opaque next cursor when more rows exist.</returns>
    Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeGovernanceAuditResponse>>> ListGovernanceAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default);

    /// <summary>Returns a bounded page of sanitized fee-schedule history.</summary>
    /// <param name="limit">Requested page size; implementations clamp it to the public maximum.</param>
    /// <param name="cursor">Opaque cursor returned by the preceding page, or null for the newest page.</param>
    /// <param name="ct">Cancels the read without changing policy state.</param>
    /// <returns>A newest-first page of typed snapshots; raw persisted JSON is never returned.</returns>
    Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeFeeAuditResponse>>> ListFeeAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default);

    /// <summary>Returns a bounded page of sanitized treasury-routing history.</summary>
    /// <param name="limit">Requested page size; implementations clamp it to the public maximum.</param>
    /// <param name="cursor">Opaque cursor returned by the preceding page, or null for the newest page.</param>
    /// <param name="ct">Cancels the read without changing policy state.</param>
    /// <returns>A newest-first page of typed routing snapshots and an optional next cursor.</returns>
    Task<AZOAResult<NodeTransparencyPageResponse<PublicNodeTreasuryAuditResponse>>> ListTreasuryAuditAsync(
        int limit = 50,
        string? cursor = null,
        CancellationToken ct = default);

    /// <summary>Returns a bounded signed checkpoint over the redacted combined audit history.</summary>
    /// <param name="ct">Cancels the read without changing governance policy.</param>
    /// <returns>An unavailable result until the operator enables dedicated node-identity checkpoints.</returns>
    Task<AZOAResult<NodeTransparencyHistoryDocument>> GetAuditHistoryCheckpointAsync(
        CancellationToken ct = default);
}
