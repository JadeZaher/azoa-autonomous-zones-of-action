namespace AZOA.WebAPI.Models.Responses;

public class NftResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? OwnerAvatarId { get; set; }
    public string ChainId { get; set; } = string.Empty;
    public string? TokenId { get; set; }
    public string AssetType { get; set; } = "NFT";
    public NftMetadata Metadata { get; set; } = new();
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public bool IsActive { get; set; }
}

public class NftMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? ExternalUrl { get; set; }
    public string? AnimationUrl { get; set; }
    public List<NftAttribute> Attributes { get; set; } = new();
}

public class NftAttribute
{
    public string TraitType { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? DisplayType { get; set; }
}

public class NftQueryRequest
{
    public Guid? OwnerAvatarId { get; set; }
    public string? ChainId { get; set; }
    public string? TokenId { get; set; }
    public string? Name { get; set; }
}
