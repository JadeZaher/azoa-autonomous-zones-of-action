using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

public interface IWallet
{
    Guid Id { get; set; }
    Guid UserId { get; set; }
    string ChainType { get; set; }
    string Address { get; set; }
    string? PublicKey { get; set; }
    string? Label { get; set; }
    bool IsDefault { get; set; }
    WalletType WalletType { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    string? EncryptedPrivateKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    string? EncryptedSeedPhrase { get; set; }
    DateTime CreatedDate { get; set; }
}
