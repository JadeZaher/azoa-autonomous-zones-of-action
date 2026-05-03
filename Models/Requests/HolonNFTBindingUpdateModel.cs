namespace OASIS.WebAPI.Models.Requests;

public class HolonNFTBindingUpdateModel
{
    public string? Role { get; set; }
    public string? PermissionLevel { get; set; }
    public Dictionary<string, string>? Permissions { get; set; }
    public bool? IsActive { get; set; }
}