namespace AZOA.WebAPI.Models.Requests;

public class NftMintRequest
{
    public Guid WalletId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? MetadataUri { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AZOA.WebAPI.Models.Blockchain.NftProtocolCostAuthorization? ProtocolCostAuthorization { get; set; }
    public string? TokenId { get; set; }
    public string? ImageUri { get; set; }
    public string? ExternalUri { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
