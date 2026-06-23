namespace AZOA.WebAPI.Models.Requests;

public class NftMintRequest
{
    public Guid WalletId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? TokenId { get; set; }
    public string? ImageUri { get; set; }
    public string? ExternalUri { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
