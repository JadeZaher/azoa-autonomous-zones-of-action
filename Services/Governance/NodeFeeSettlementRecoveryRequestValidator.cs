namespace AZOA.WebAPI.Services.Governance;

/// <summary>Shared validation for bounded, clock-explicit settlement recovery callers.</summary>
internal static class NodeFeeSettlementRecoveryRequestValidator
{
    public static string? Validate(NodeFeeSettlementRecoveryRequest? request)
    {
        if (request is null)
            return "Node fee settlement recovery request is required.";
        if (request.Now == default)
            return "Node fee settlement recovery requires an explicit clock value.";
        if (request.BatchSize is < 1 or > 100)
            return "Node fee settlement recovery batch size must be between 1 and 100.";
        if (request.LeaseDuration < TimeSpan.FromSeconds(1)
            || request.LeaseDuration > TimeSpan.FromMinutes(15))
        {
            return "Node fee settlement recovery lease duration must be between one second and fifteen minutes.";
        }

        if (request.RetryDelay < TimeSpan.FromSeconds(1)
            || request.RetryDelay > TimeSpan.FromDays(1))
        {
            return "Node fee settlement recovery retry delay must be between one second and one day.";
        }

        return null;
    }
}
