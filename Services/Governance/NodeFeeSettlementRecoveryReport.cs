namespace AZOA.WebAPI.Services.Governance;

/// <summary>Counts one inert recovery sweep; no count represents a chain submission.</summary>
public sealed record NodeFeeSettlementRecoveryReport(
    int Candidates,
    int Claimed,
    int Deferred,
    int Contended);
