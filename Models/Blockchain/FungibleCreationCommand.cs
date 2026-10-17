using AZOA.WebAPI.Models.Actions;

namespace AZOA.WebAPI.Models.Blockchain;

public sealed record FungibleCreationCommand(Guid ActorUserId, Guid? TenantId, string RequestKey,
    Guid WalletId, string WalletAddress, bool WalletProvisioned, string MintTarget, Guid? HolonId,
    FungibleCreationIntent Intent, ActionEffectAuthority? ActionAuthority = null, string? ClientRequestHash = null,
    AZOA.WebAPI.Models.Signing.SigningAdmissionAuthority? SigningAuthority = null, long? AdmissionPolicyVersion = null,
    AZOA.WebAPI.Models.Signing.PlatformGenesisAuthority? GenesisAuthority = null);

public sealed record FungibleCreationView(string OperationId, long Version, string State, string ChainType,
    string Network, string? AssetId, string? TransactionId, string? Detail, FungibleCreationQuote? Quote,
    Guid WalletId, string WalletAddress, bool WalletProvisioned, Guid? RequestedHolonId = null, string Purpose = "Participant");
