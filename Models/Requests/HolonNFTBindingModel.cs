namespace AZOA.WebAPI.Models.Requests;

public class HolonNFTBindingModel
{
    public string Role { get; set; } = string.Empty;
    public string? PermissionLevel { get; set; }
    public Dictionary<string, string> Permissions { get; set; } = new();
}