namespace AZOA.WebAPI.Services.Admin;

/// <summary>Environment-seeded credentials for the durable node operator.</summary>
public sealed class NodeOperatorOptions
{
    public const string SectionName = "NodeOperator";

    public string? Username { get; set; }

    public string? Password { get; set; }

    public long CredentialRevision { get; set; }

    public int SessionMinutes { get; set; } = 20;
}
