namespace AZOA.WebAPI.Models.Requests;

public class WalletNFTBindingModel
{
    public string BindingType { get; set; } = string.Empty;
    public string? AccessLevel { get; set; }
    public Dictionary<string, string> AccessPermissions { get; set; } = new();
}