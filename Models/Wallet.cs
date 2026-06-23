using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models;

public class Wallet : IWallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AvatarId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
    public WalletType WalletType { get; set; } = WalletType.External;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EncryptedPrivateKey { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? EncryptedSeedPhrase { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
