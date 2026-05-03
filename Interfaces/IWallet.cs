namespace OASIS.WebAPI.Interfaces;

public interface IWallet
{
    Guid Id { get; set; }
    Guid AvatarId { get; set; }
    string ChainType { get; set; }
    string Address { get; set; }
    string? PublicKey { get; set; }
    string? Label { get; set; }
    bool IsDefault { get; set; }
    DateTime CreatedDate { get; set; }
}
