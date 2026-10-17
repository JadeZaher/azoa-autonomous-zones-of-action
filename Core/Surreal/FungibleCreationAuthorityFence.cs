using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Selects the mutually exclusive ordinary signing or restricted genesis authority.</summary>
public static class FungibleCreationAuthorityFence
{
    public static IReadOnlyList<SurrealQuery>? Create(FungibleCreationCommand command, string? currentAuthorityJson, string prefix,
        bool freshParticipant = false)
    {
        if (command.GenesisAuthority is { } original)
        {
            var purpose = currentAuthorityJson is null ? original : JsonSerializer.Deserialize<PlatformGenesisAuthority>(currentAuthorityJson);
            if (purpose is null || (purpose with { Credential = original.Credential }) != original) return null;
            return PlatformGenesisFence.Create(command, purpose);
        }
        var authority = currentAuthorityJson is null ? command.SigningAuthority : JsonSerializer.Deserialize<SigningAdmissionAuthority>(currentAuthorityJson);
        if (authority is null || (freshParticipant && authority.Kind == "Workflow")
            || authority.Credential.ActorUserId != command.ActorUserId || authority.Credential.TenantId != command.TenantId
            || authority.Wallet.Id != command.WalletId || authority.Wallet.Address != command.WalletAddress
            || authority.Wallet.ChainType != command.Intent.ChainType || (authority.Kind == "Workflow" && authority.Action != command.ActionAuthority)) return null;
        var fence = SigningAuthorityFence.Create(authority, [AzoaScopes.TokenCreateSign], prefix);
        if (fence is null) return null;
        var statements = fence.ToList();
        if (authority.Kind != "Workflow" && command.ActionAuthority is { } action)
            statements.AddRange(ActionExecutionFence.Create(action.RunId.ToString("N"), action.NodeId.ToString("N"),
                command.ActorUserId.ToString("N"), command.TenantId?.ToString("N"), action.IntentHash));
        return statements;
    }
}
