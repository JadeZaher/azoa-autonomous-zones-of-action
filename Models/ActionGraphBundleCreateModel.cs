namespace AZOA.WebAPI.Models;

public class ActionGraphBundleCreateModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public Guid? UserId { get; set; }
}
