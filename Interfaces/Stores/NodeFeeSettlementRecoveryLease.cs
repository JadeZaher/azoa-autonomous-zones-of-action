using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Opaque ownership proof returned by the durable recovery claim.</summary>
public sealed record NodeFeeSettlementRecoveryLease(
    string SettlementId,
    string LeaseToken,
    long StateVersion)
{
    /// <summary>Creates the lease proof from a successfully claimed settlement.</summary>
    public static NodeFeeSettlementRecoveryLease FromClaim(NodeFeeSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        return new NodeFeeSettlementRecoveryLease(
            settlement.Id,
            settlement.LeaseToken
                ?? throw new InvalidOperationException("A recovery claim must return its lease token."),
            settlement.StateVersion);
    }
}
