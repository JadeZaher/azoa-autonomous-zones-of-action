using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Recognizes only an empty, terminal, pre-admission NFT cancellation marker.</summary>
public static class NftCancellationTombstone
{
    public static bool Matches(NftLifecycle row, NftLifecycleCommand command)
        => row.State == "Cancelled" && row.Stage == "Cancellation" && command.ActionAuthority is null && Empty(row, command);

    public static bool MatchesWorkflowRejection(NftLifecycle row, NftLifecycleCommand command)
        => row.State == "Failed" && row.Stage == "AdmissionFailure" && command.ActionAuthority is { } action
            && action.ActorUserId == command.Actor && (action.ActingTenantId ?? Guid.Empty) == command.ActingTenantId
            && row.Id == NftLifecycleIdentity.ForRequest(command.Actor, row.Operation,
                $"action:{action.RunId:N}:{action.NodeId:N}:nft", command.ActingTenantId) && Empty(row, command);

    private static bool Empty(NftLifecycle row, NftLifecycleCommand command)
    {
        if (command.Actor == Guid.Empty
            || row.Operation is not ("Mint" or "Transfer" or "Burn" or "Revoke" or "Refund")
            || !Guid.TryParseExact(row.Id, "N", out _) || row.ActorUserId != command.Actor.ToString("N")
            || row.RecipientUserId != row.ActorUserId || row.NftId != Guid.Empty.ToString("N")
            || row.ChainType != string.Empty || row.Version != 0 || row.PreparationGeneration != 0 || row.ConfirmedStageCount != 0
            || row.PreparedTransactionJson is not null || row.TransactionHistoryJson != "[]" || row.StageConsentsJson != "[]"
            || row.ReplacementCostConsentsJson != "[]"
            || row.ReplacementCostRetirementsJson != "[]" || row.ActorContinuationsJson != "[]"
            || row.AssetId is not null || row.ProtocolIdentity is not null || row.ContractAddress is not null
            || row.TokenStandard is not null || row.CreationTransactionId is not null || row.FinalTransactionId is not null
            || row.RequestHash != NftLifecycleIdentity.Fingerprint(row.RequestJson)) return false;
        var empty = new NftLifecycleCommand(command.Actor, command.Actor, Guid.Empty, Guid.Empty,
            string.Empty, string.Empty, new UserNFTMintModel(), -1, command.ActingTenantId, ActionAuthority: command.ActionAuthority);
        return row.RequestJson == JsonSerializer.Serialize(empty) && JsonSerializer.Serialize(command) == row.RequestJson;
    }
}
