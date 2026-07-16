namespace AZOA.WebAPI.Services.Governance;

/// <summary>Bounded, clock-explicit input for the inert settlement recovery sweep.</summary>
public sealed record NodeFeeSettlementRecoveryRequest(
    DateTimeOffset Now,
    int BatchSize,
    TimeSpan LeaseDuration,
    TimeSpan RetryDelay);
