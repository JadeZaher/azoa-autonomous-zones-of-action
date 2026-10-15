namespace AZOA.WebAPI.Core;

[Flags]
public enum SearchableEntityType
{
    None = 0,
    User = 1,
    Holon = 2,
    Wallet = 4,
    BlockchainOperation = 8,
    ActionGraphBundle = 16,
    All = User | Holon | Wallet | BlockchainOperation | ActionGraphBundle
}
