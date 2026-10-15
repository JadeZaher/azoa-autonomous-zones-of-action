namespace AZOA.WebAPI.Models.Requests;

public class WalletQueryRequest
{
    public Guid? UserId { get; set; }
    public string? ChainType { get; set; }
    public bool? IsDefault { get; set; }
}
