namespace AZOA.WebAPI.Services.Actions.Designer;

public sealed class WorkflowDesignerOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "anthropic/claude-sonnet-4.6";
    public int RequestsPerHour { get; set; } = 12;
    public const string ClientName = "workflow-designer";
}
