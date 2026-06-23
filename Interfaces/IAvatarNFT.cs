namespace AZOA.WebAPI.Interfaces;

public interface IAvatarNFT
{
    Guid Id { get; set; }
    Guid AvatarId { get; set; }
    string NFTContractAddress { get; set; }
    string TokenId { get; set; }
    string ChainType { get; set; }
    string TokenStandard { get; set; }
    string MetadataURI { get; set; }
    string? ImageURI { get; set; }
    string? Name { get; set; }
    string? Description { get; set; }
    Dictionary<string, string> Attributes { get; set; }
    decimal RoyaltyPercentage { get; set; }
    string? RoyaltyRecipient { get; set; }
    bool IsSoulbound { get; set; }
    bool IsTransferable { get; set; }
    DateTime MintedDate { get; set; }
    DateTime? LastTransferDate { get; set; }
    string? CurrentOwner { get; set; }
    bool IsActive { get; set; }
}