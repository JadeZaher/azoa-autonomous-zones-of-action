namespace AZOA.WebAPI.Interfaces;

public interface IWalletNFTBinding
{
    Guid Id { get; set; }
    Guid WalletId { get; set; }
    Guid AvatarNFTId { get; set; }
    string BindingType { get; set; }
    string? AccessLevel { get; set; }
    Dictionary<string, string> AccessPermissions { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? LastUpdatedDate { get; set; }
    bool IsActive { get; set; }
}