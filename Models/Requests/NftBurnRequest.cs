namespace AZOA.WebAPI.Models.Requests;

public class NftBurnRequest
{
    public Guid WalletId { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
}
