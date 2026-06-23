namespace AZOA.WebAPI.Interfaces;

public interface IMintOperation : IBlockchainOperation
{
    string? TokenUri { get; set; }
    ulong Amount { get; set; }
    string? AssetType { get; set; }
}
