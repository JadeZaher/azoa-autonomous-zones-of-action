using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Resolves exact participant consent without substituting later caller authority.</summary>
public static class NftStageConsentIdentity
{
    public static bool Required(NftLifecycle row, NftLifecycleCommand command)
        => row.Stage == NftTransactionKinds.Accept
            || row.Stage == NftTransactionKinds.Burn && command.ActorWalletId != command.RecipientWalletId
            || row.Operation == "Refund" && row.Stage == NftTransactionKinds.Transfer;

    public static IReadOnlyList<NftStageConsent> Read(NftLifecycle row)
        => JsonSerializer.Deserialize<NftStageConsent[]>(row.StageConsentsJson)
            ?? throw new InvalidOperationException("NFT stage consent history is unreadable.");

    public static NftStageConsent? Current(NftLifecycle row)
        => Read(row).SingleOrDefault(item => item.LifecycleId == row.Id && item.Stage == row.Stage
            && item.Generation == row.PreparationGeneration);

    public static bool ReplacementReady(NftLifecycle row)
    {
        if (row.State != "Preparing" || row.PreparedTransactionJson is not null || row.PreparationGeneration <= 0) return false;
        var previous = (JsonSerializer.Deserialize<NftTransactionAttempt[]>(row.TransactionHistoryJson) ?? []).LastOrDefault();
        return previous is { Verdict: "ExpiredAbsent" or "FailedOnChain" }
            && previous.Transaction.Intent.Authorization is { } authorization
            && authorization.LifecycleId == row.Id && authorization.Stage == row.Stage
            && authorization.Operation == row.Operation && authorization.PreparationGeneration == row.PreparationGeneration - 1
            && previous.Transaction.Intent.Kind == row.Stage && previous.Transaction.Intent.ChainType == row.ChainType
            && previous.Transaction.Intent.Network.ToString() == row.Network
            && (row.ProtocolIdentity is null || previous.Transaction.ProtocolIdentity == row.ProtocolIdentity)
            && (row.ContractAddress is null || previous.Transaction.ContractAddress == row.ContractAddress);
    }

    public static bool NeedsReplacementConsent(NftLifecycle row, NftLifecycleCommand command)
        => Required(row, command) && Current(row) is null && ReplacementReady(row);

    public static bool RecipientSigns(NftLifecycle row, NftLifecycleCommand command)
        => row.Stage == NftTransactionKinds.Accept && row.Operation != "Refund"
            || row.Stage == NftTransactionKinds.Burn && command.ActorWalletId != command.RecipientWalletId
            || row.Operation == "Refund" && row.Stage == NftTransactionKinds.Transfer;

    public static bool Matches(NftLifecycle row, NftLifecycleCommand command, NftStageConsent consent)
    {
        var recipient = RecipientSigns(row, command);
        var authority = consent.Authority;
        if (!Required(row, command) || consent.LifecycleId != row.Id || consent.Stage != row.Stage
            || consent.Generation != row.PreparationGeneration || authority.Kind is not ("DirectJwt" or "DirectApiKey")
            || authority.Action is not null || authority.WorkflowAuthorizationId is not null
            || authority.Credential.ActorUserId != (recipient ? command.Recipient : command.Actor)
            || authority.Wallet.OwnerUserId != authority.Credential.ActorUserId
            || authority.Wallet.Id != (recipient ? command.RecipientWalletId : command.ActorWalletId)
            || authority.Wallet.Address != (recipient ? command.RecipientAddress : command.ActorAddress)
            || authority.Wallet.ChainType != row.ChainType) return false;
        var intent = NftLifecycleIntentFactory.Create(row, command, Enum.Parse<ChainNetwork>(row.Network),
            command.PlatformAuthorityAddress, authority.Credential.TenantId ?? Guid.Empty);
        return intent is not null && consent.IntentHash == Hash(row, intent);
    }

    public static bool CanAppend(NftLifecycle before, NftLifecycle after)
    {
        var old = Read(before);
        var next = Read(after);
        if (next.Count != old.Count + 1 || old.Where((item, index) =>
            JsonSerializer.Serialize(item) != JsonSerializer.Serialize(next[index])).Any()
            || next.Select(item => (item.LifecycleId, item.Stage, item.Generation)).Distinct().Count() != next.Count
            || after.State != "Preparing" || after.PreparedTransactionJson is not null
            || before.PreparationGeneration != after.PreparationGeneration
            || before.Id != after.Id || before.RequestJson != after.RequestJson || before.RequestHash != after.RequestHash
            || before.ActorUserId != after.ActorUserId || before.RecipientUserId != after.RecipientUserId
            || before.NftId != after.NftId || before.Operation != after.Operation || before.ChainType != after.ChainType
            || before.Network != after.Network || before.AssetId != after.AssetId || before.ProtocolIdentity != after.ProtocolIdentity
            || before.ContractAddress != after.ContractAddress || before.TokenStandard != after.TokenStandard
            || before.CreationTransactionId != after.CreationTransactionId || before.FinalTransactionId != after.FinalTransactionId
            || before.ConfirmedStageCount != after.ConfirmedStageCount || before.TransactionHistoryJson != after.TransactionHistoryJson) return false;
        var request = next[^1].Request;
        if (request is null || request.RequestId == Guid.Empty || request.ExpectedVersion != before.Version
            || request.State != before.State || request.Stage != before.Stage || request.Generation != before.PreparationGeneration
            || old.Any(item => item.Request?.RequestId == request.RequestId)) return false;
        var expected = before.State switch
        {
            "AwaitingCreatorDestruction" => NftTransactionKinds.Burn,
            "AwaitingRefundAuthorization" => NftTransactionKinds.Transfer,
            "AwaitingRecipientAcceptance" or "AwaitingRefundDestinationAcceptance" => NftTransactionKinds.Accept,
            _ => null,
        };
        if (expected is null && ReplacementReady(before)) expected = before.Stage;
        return expected == after.Stage && next[^1].LifecycleId == after.Id
            && next[^1].Stage == after.Stage && next[^1].Generation == after.PreparationGeneration;
    }

    public static string Hash(NftLifecycle row, NftTransactionIntent intent)
        => CanonicalJson.Hash(JsonSerializer.SerializeToElement(new
        {
            row.Id, row.Stage, row.PreparationGeneration, row.ChainType, row.Network,
            row.ProtocolIdentity, row.ContractAddress, row.TokenStandard, intent,
        }));
}
