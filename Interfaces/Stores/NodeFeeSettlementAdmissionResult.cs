using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Describes the atomic admission of a parent claim and immutable settlement.</summary>
public sealed record NodeFeeSettlementAdmissionResult(
    NodeFeeSettlement Settlement,
    IdempotencyRecord ParentClaim,
    NodeFeeSettlementAdmissionDisposition Disposition);

/// <summary>Outcome of an atomic parent-claim and settlement admission.</summary>
public enum NodeFeeSettlementAdmissionDisposition
{
    Created,
    Replayed,
}
