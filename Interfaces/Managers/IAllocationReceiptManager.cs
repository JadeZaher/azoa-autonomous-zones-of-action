// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Caller-authorized receipt and reconciliation surface for an allocation. The
/// controller must construct <see cref="AllocationReceiptRequest"/> solely from
/// authenticated API-key claims and the required <c>Idempotency-Key</c> header.
/// </summary>
public interface IAllocationReceiptManager
{
    /// <summary>
    /// Returns the caller's secret-free allocation receipt. The manager derives
    /// the internal ledger key locally and verifies any matching opaque operation
    /// against its persisted initiator API key and avatar. A valid failed or
    /// in-progress ledger remains readable when no operation was persisted.
    /// Missing ledger bindings, foreign operations, and initiator mismatches must
    /// all return the same not-found-shaped result.
    /// </summary>
    /// <param name="request">Claims-derived API-key caller context and required client key.</param>
    /// <param name="ct">Cancellation token for read-only storage access.</param>
    Task<AZOAResult<AllocationReceiptResponse>> GetAsync(
        AllocationReceiptRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Re-observes a caller-authorized operation from chain truth and returns its
    /// latest receipt. This never resubmits, retries, or otherwise executes an
    /// allocation. A receipt with no durable operation is returned unchanged.
    /// Missing ledger bindings, foreign operations, and initiator mismatches must
    /// all return the same not-found-shaped result.
    /// </summary>
    /// <param name="request">Claims-derived API-key caller context and required client key.</param>
    /// <param name="ct">Cancellation token for the observation-only pass.</param>
    Task<AZOAResult<AllocationReceiptResponse>> ReconcileAsync(
        AllocationReceiptRequest request,
        CancellationToken ct = default);
}
