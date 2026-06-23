namespace AZOA.WebAPI.Models.Requests;

public class NftTransferRequest
{
    public Guid TargetAvatarId { get; set; }
    public Guid WalletId { get; set; }
    public string? Memo { get; set; }
}
