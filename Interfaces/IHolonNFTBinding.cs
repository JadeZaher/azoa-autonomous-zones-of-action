namespace AZOA.WebAPI.Interfaces;

public interface IHolonNFTBinding
{
    Guid Id { get; set; }
    Guid HolonId { get; set; }
    Guid AvatarNFTId { get; set; }
    string Role { get; set; }
    string? PermissionLevel { get; set; }
    Dictionary<string, string> Permissions { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? LastUpdatedDate { get; set; }
    bool IsActive { get; set; }
}