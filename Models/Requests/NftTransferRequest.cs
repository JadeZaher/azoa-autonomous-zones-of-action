namespace AZOA.WebAPI.Models.Requests;

public class NftTransferRequest
{
    public Guid TargetUserId { get; set; }
    public Guid WalletId { get; set; }
    public string? Memo { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
}
