using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds recipient-stage Solana costs to the paying participant and exact stage; see Services/Users/AGENTS.md §NFT recipient-stage costs.</summary>
public static class NftStageCostContract
{
    /// <summary>Solana stages whose message can allocate a destination holding (rent) and always spends network fees.</summary>
    public static bool Required(NftLifecycle row)
        => row.ChainType == "Solana" && row.Stage is NftTransactionKinds.Accept or NftTransactionKinds.Transfer or NftTransactionKinds.ReturnForBurn;

    /// <summary>The fee payer of the stage message: the accepting recipient, otherwise the requester.</summary>
    public static bool PayerIsRecipient(NftLifecycle row) => row.Stage == NftTransactionKinds.Accept && row.Operation != "Refund";

    public static (Guid User, Guid WalletId, string Address) Payer(NftLifecycle row, NftLifecycleCommand command)
        => PayerIsRecipient(row) ? (command.Recipient, command.RecipientWalletId, command.RecipientAddress)
            : (command.Actor, command.ActorWalletId, command.ActorAddress);

    /// <summary>Tenant the payer must accept under: the payer's own stage consent when the payer signs it, else the requester's.</summary>
    public static Guid? PayerTenant(NftLifecycle row, NftLifecycleCommand command)
    {
        var payerSigns = NftStageConsentIdentity.Required(row, command)
            && NftStageConsentIdentity.RecipientSigns(row, command) == PayerIsRecipient(row);
        if (payerSigns) return NftStageConsentIdentity.Current(row) is { } consent ? consent.Authority.Credential.TenantId ?? Guid.Empty : null;
        return NftActorContinuationIdentity.TakenOver(row, command) ? Guid.Empty : command.ActingTenantId;
    }

    public static IReadOnlyList<NftStageCostConsent> Read(NftLifecycle row)
        => row.StageCostConsentsJson is null ? []
            : JsonSerializer.Deserialize<NftStageCostConsent[]>(row.StageCostConsentsJson)
                ?? throw new InvalidOperationException("NFT stage cost history is unreadable.");

    /// <summary>The latest acceptance for the current stage and generation; a later re-quote supersedes it.</summary>
    public static NftStageCostConsent? Current(NftLifecycle row)
        => Read(row).LastOrDefault(item => item.Quote.LifecycleId == row.Id && item.Quote.Stage == row.Stage
            && item.Quote.PreparationGeneration == row.PreparationGeneration);

    /// <summary>Costs may be quoted or accepted only before any wire exists, after the stage's own participant consent.</summary>
    public static bool Eligible(NftLifecycle row, NftLifecycleCommand command)
        => Required(row) && row.State == "Preparing" && row.PreparedTransactionJson is null
            && (!NftStageConsentIdentity.Required(row, command) || NftStageConsentIdentity.Current(row) is not null);

    public static bool Reviews(NftLifecycle row, NftStageAcceptance request)
        => request.RequestId != Guid.Empty && request.ExpectedVersion == row.Version && request.State == row.State
            && request.Stage == row.Stage && request.Generation == row.PreparationGeneration;

    public static string DraftHash(NftLifecycle row)
        => CanonicalJson.Hash(new { domain = "nft-stage-cost-draft/1", row.Id, row.Stage, row.PreparationGeneration, row.RequestHash });

    /// <summary>Signing is admitted only with a binding acceptance; a prepared wire must carry exactly that receipt.</summary>
    public static bool Ready(NftLifecycle row, NftLifecycleCommand command)
    {
        if (Current(row) is not { } consent || !Matches(row, command, consent)) return false;
        if (row.PreparedTransactionJson is null) return true;
        var prepared = JsonSerializer.Deserialize<PreparedNftTransaction>(row.PreparedTransactionJson);
        return prepared?.Intent.ProtocolCostQuote == consent.Quote;
    }

    /// <summary>Store precondition for authority-bearing writes: signing steps of a cost-bearing stage need <see cref="Ready"/>.</summary>
    public static bool AdmitsWrite(NftLifecycle before, NftLifecycle after)
    {
        if (!Required(after) || before.StageConsentsJson != after.StageConsentsJson
            || before.ActorContinuationsJson != after.ActorContinuationsJson
            || before.StageCostConsentsJson != after.StageCostConsentsJson) return true;
        return Ready(after, JsonSerializer.Deserialize<NftLifecycleCommand>(after.RequestJson)!);
    }

    public static bool Matches(NftLifecycle row, NftLifecycleCommand command, NftStageCostConsent consent)
    {
        var quote = consent.Quote;
        var authority = consent.Authority;
        var payer = Payer(row, command);
        if (!Required(row) || !NftProtocolCostContract.IsAuthorized(quote, consent.Request.Costs)
            || consent.Request.Stage.Stage != row.Stage || consent.Request.Stage.Generation != row.PreparationGeneration
            || quote.LifecycleId != row.Id || quote.Stage != row.Stage || quote.PreparationGeneration != row.PreparationGeneration
            || PayerTenant(row, command) is not { } tenant || quote.ActingTenantId != tenant
            || quote.ActorUserId != payer.User || quote.PayerWalletId != payer.WalletId || quote.PayerAddress != payer.Address
            || quote.ChainType != row.ChainType || quote.Network.ToString() != row.Network || quote.DraftHash != DraftHash(row)
            || (row.ProtocolIdentity is not null && row.ProtocolIdentity != quote.PhysicalNetworkIdentity)
            || authority.Kind is not ("DirectJwt" or "DirectApiKey") || authority.Action is not null || authority.WorkflowAuthorizationId is not null
            || authority.Credential.ActorUserId != payer.User || (authority.Credential.TenantId ?? Guid.Empty) != tenant
            || authority.Wallet.Id != payer.WalletId || authority.Wallet.OwnerUserId != payer.User
            || authority.Wallet.Address != payer.Address || authority.Wallet.ChainType != row.ChainType) return false;
        var intent = NftLifecycleIntentFactory.Create(row, command, Enum.Parse<ChainNetwork>(row.Network),
            command.PlatformAuthorityAddress, tenant);
        return intent is not null && quote.QuotedIntentHash == NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId);
    }

    /// <summary>One appended acceptance for the reviewed version; nothing else on the row changes.</summary>
    public static bool CanAppend(NftLifecycle before, NftLifecycle after)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(before.RequestJson)!;
        if (!Eligible(before, command)) return false;
        var old = Read(before);
        var next = Read(after);
        if (next.Count != old.Count + 1 || old.Where((item, index) =>
            JsonSerializer.Serialize(item) != JsonSerializer.Serialize(next[index])).Any()) return false;
        var request = next[^1].Request.Stage;
        if (!Reviews(before, request) || old.Any(item => item.Request.Stage.RequestId == request.RequestId)) return false;
        var unchanged = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(after))!;
        unchanged.StageCostConsentsJson = before.StageCostConsentsJson;
        unchanged.UpdatedAt = before.UpdatedAt;
        return CanonicalJson.Hash(unchanged) == CanonicalJson.Hash(before) && Matches(after, command, next[^1]);
    }
}
