namespace OASIS.WebAPI.Models.Requests;

public class WalletCreateModel
{
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
}
