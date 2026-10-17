namespace AZOA.WebAPI.Models.HostedSessions;

public sealed class HostedSessionReconciliationRequest
{
    public bool OperatorReviewedSubjectAndProvider { get; init; }
    public bool ConfirmedProviderAbsence { get; init; }
    public string? Reason { get; init; }
    public string? ProviderSessionId { get; init; }
    public string? ProviderCorrelationId { get; init; }
    public string? RedirectUrl { get; init; }
}
