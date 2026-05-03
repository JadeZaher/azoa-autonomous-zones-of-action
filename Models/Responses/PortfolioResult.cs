namespace OASIS.WebAPI.Models.Responses;

public class PortfolioResult
{
    public Guid WalletId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Symbol { get; set; } = "SOL";
    public List<NftHolding> Nfts { get; set; } = new();
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

public class NftHolding
{
    public Guid HolonId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TokenId { get; set; }
    public string? ImageUri { get; set; }
}
