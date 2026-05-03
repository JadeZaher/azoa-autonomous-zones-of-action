namespace OASIS.WebAPI.Interfaces;

public interface IExchangeOperation : IBlockchainOperation
{
    Guid? SourceHolonId { get; set; }
    Guid? TargetHolonId { get; set; }
    string? ExchangeRate { get; set; }
}
