namespace AZOA.WebAPI.Settings;

/// <summary>Tenant business verification policy; see Services/Kyb/AGENTS.md.</summary>
public sealed class KybSettings
{
    public const string SectionName = "Kyb";
    public string? ConfigId { get; set; }
    public string? ReturnUrl { get; set; }
    public string PolicyVersion { get; set; } = string.Empty;
    public int AttemptLifetimeHours { get; set; } = 72;
    public int ApprovalLifetimeDays { get; set; } = 365;
}
