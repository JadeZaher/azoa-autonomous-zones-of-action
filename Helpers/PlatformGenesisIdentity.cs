using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds a platform-purpose authorization to its original frozen definition.</summary>
public static class PlatformGenesisIdentity
{
    public static string DefinitionHash(FungibleCreationIntent intent, string operationKey)
        => CanonicalJson.Hash(new { intent.ChainType, Network = intent.Network.ToString(), intent.Name, intent.UnitName,
            intent.Decimals, intent.TotalBaseUnits, intent.CreatorAddress, OperationKey = operationKey });

    public static bool Matches(FungibleCreationCommand command, PlatformGenesisAuthority purpose)
        => purpose.Version == 1 && command.SigningAuthority is null && command.ActionAuthority is null
            && command.HolonId is null && command.WalletId == Guid.Empty && !command.WalletProvisioned
            && command.ActorUserId == NodeOperatorIdentity.UserId && command.TenantId is null && command.MintTarget == "PlatformReserve"
            && purpose.Credential.ActorUserId == command.ActorUserId && purpose.Credential.TenantId is null
            && purpose.Credential.TokenUse == AzoaClaims.TokenUseNodeOperator && !purpose.Credential.OwnerAuthority
            && purpose.PlatformTokenId == PlatformToken.RecordIdFor(command.Intent.ChainType, command.Intent.Network.ToString())
            && purpose.GenesisOperationKey == command.RequestKey && purpose.DefinitionHash == DefinitionHash(command.Intent, command.RequestKey)
            && !string.IsNullOrWhiteSpace(purpose.ProtocolIdentity) && command.WalletAddress == command.Intent.CreatorAddress
            && command.Intent.SigningContext.IsPlatform && command.Intent.ActorUserId == command.ActorUserId && command.Intent.TenantId is null
            && !command.Intent.DefaultFrozen && command.Intent.ManagerAddress == command.Intent.CreatorAddress
            && command.Intent.ReserveAddress == command.Intent.CreatorAddress && command.Intent.FreezeAddress == command.Intent.CreatorAddress
            && command.Intent.ClawbackAddress == command.Intent.CreatorAddress && command.Intent.Url is null && command.Intent.MetadataHashBase64 is null;
}
