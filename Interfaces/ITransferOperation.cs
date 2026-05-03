namespace OASIS.WebAPI.Interfaces;

public interface ITransferOperation : IBlockchainOperation
{
    Guid? SourceHolonId { get; set; }
    string? RecipientAddress { get; set; }
}
