namespace OASIS.WebAPI.Interfaces;

public interface IMintOperation : IBlockchainOperation
{
    string? TokenUri { get; set; }
    int Amount { get; set; }
    string? AssetType { get; set; }
}
