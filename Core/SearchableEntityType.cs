namespace AZOA.WebAPI.Core;

[Flags]
public enum SearchableEntityType
{
    None = 0,
    Avatar = 1,
    Holon = 2,
    Wallet = 4,
    BlockchainOperation = 8,
    STARODK = 16,
    All = Avatar | Holon | Wallet | BlockchainOperation | STARODK
}
