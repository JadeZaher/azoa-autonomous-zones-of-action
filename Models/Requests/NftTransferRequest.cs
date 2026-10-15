namespace AZOA.WebAPI.Models.Requests;

public class NftTransferRequest
{
    public Guid TargetUserId { get; set; }
    public Guid WalletId { get; set; }
    public string? Memo { get; set; }
}
