namespace AZOA.WebAPI.Models.Requests;

public sealed class NodeOperatorLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class UpdateKycProviderProfileRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string AdapterKey { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public string AssuranceLevel { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
}

public sealed class SelectTenantKycProviderRequest
{
    public string ProviderKey { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
}

public sealed class OperatorKycDecisionRequest
{
    public string Decision { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Reason { get; set; }
}
