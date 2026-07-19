namespace AZOA.WebAPI.Models.Responses;

/// <summary>Minimal KYC state projection for user profiles and consented tenants.</summary>
public sealed class KycStatusSummaryResponse
{
    public bool HasSubmission { get; init; }
    public bool IsVerified { get; init; }
    public string? Status { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
}
