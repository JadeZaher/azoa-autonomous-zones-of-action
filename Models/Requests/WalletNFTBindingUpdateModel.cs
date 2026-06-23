namespace AZOA.WebAPI.Models.Requests;

public class WalletNFTBindingUpdateModel
{
    public string? BindingType { get; set; }
    public string? AccessLevel { get; set; }
    public Dictionary<string, string>? AccessPermissions { get; set; }
    public bool? IsActive { get; set; }
}