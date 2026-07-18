namespace AZOA.WebAPI.Services.Governance;

/// <summary>Counts one receipt-driven, observation-only settlement reconciliation sweep.</summary>
public sealed record NodeFeeSettlementAtomicGroupReconciliationReport(
    int Candidates,
    int Claimed,
    int Settled,
    int NonTerminal,
    int Deferred,
    int Contended);
