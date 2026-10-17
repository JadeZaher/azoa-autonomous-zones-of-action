using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds append-only replacement costs to an unchanged lifecycle and exact generation.</summary>
public static class NftReplacementCostContract
{
    public static IReadOnlyList<NftReplacementCostRetirement> Retirements(NftLifecycle row)
        => JsonSerializer.Deserialize<NftReplacementCostRetirement[]>(row.ReplacementCostRetirementsJson)
            ?? throw new InvalidOperationException("NFT cost retirement history is unreadable.");

    public static bool CanRetire(NftLifecycle before, NftLifecycle after)
    {
        var prior = Retirements(before);
        var next = Retirements(after);
        if (!Eligible(before) || Current(before) is not null || next.Count != prior.Count + 1
            || prior.Where((item, index) => JsonSerializer.Serialize(item) != JsonSerializer.Serialize(next[index])).Any()) return false;
        var latest = next[^1];
        if (!Reviews(before, latest.Request.Stage) || latest.Request.Costs is null
            || prior.Any(item => item.Request.Stage.RequestId == latest.Request.Stage.RequestId)
            || Read(before).Any(item => item.Request.Stage.RequestId == latest.Request.Stage.RequestId)) return false;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(before.RequestJson)!;
        var authority = latest.Authority;
        if (authority.Kind is not ("DirectJwt" or "DirectApiKey") || authority.Action is not null || authority.WorkflowAuthorizationId is not null
            || authority.Credential.ActorUserId != command.Actor || (authority.Credential.TenantId ?? Guid.Empty) != command.ActingTenantId
            || authority.Wallet.Id != command.ActorWalletId || authority.Wallet.OwnerUserId != command.Actor
            || authority.Wallet.Address != command.ActorAddress || authority.Wallet.ChainType != before.ChainType) return false;
        var unchanged = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(after))!;
        unchanged.ReplacementCostRetirementsJson = before.ReplacementCostRetirementsJson;
        unchanged.UpdatedAt = before.UpdatedAt;
        return CanonicalJson.Hash(unchanged) == CanonicalJson.Hash(before);
    }
    public static bool Reviews(NftLifecycle row, NftStageAcceptance request)
        => request.RequestId != Guid.Empty && request.ExpectedVersion == row.Version && request.State == row.State
            && request.Stage == row.Stage && request.Generation == row.PreparationGeneration;

    public static bool SamePhysicalNetwork(NftLifecycle row, NftProtocolCostQuote quote)
    {
        var prior = (JsonSerializer.Deserialize<NftTransactionAttempt[]>(row.TransactionHistoryJson) ?? []).LastOrDefault();
        return prior is not null && prior.Transaction.ProtocolIdentity == quote.PhysicalNetworkIdentity;
    }
    public static IReadOnlyList<NftReplacementCostConsent> Read(NftLifecycle row)
        => JsonSerializer.Deserialize<NftReplacementCostConsent[]>(row.ReplacementCostConsentsJson)
            ?? throw new InvalidOperationException("NFT replacement cost history is unreadable.");

    public static NftReplacementCostConsent? Current(NftLifecycle row)
        => Read(row).SingleOrDefault(item => item.Quote.LifecycleId == row.Id && item.Quote.Stage == row.Stage
            && item.Quote.PreparationGeneration == row.PreparationGeneration);

    public static bool Eligible(NftLifecycle row)
        => row.Operation == "Mint" && row.ChainType == "Solana" && row.Stage == NftTransactionKinds.Create
            && row.ConfirmedStageCount == 0 && NftStageConsentIdentity.ReplacementReady(row);

    public static bool Matches(NftLifecycle row, NftLifecycleCommand command, NftReplacementCostConsent consent)
    {
        var quote = consent.Quote;
        var authority = consent.Authority;
        if (!NftProtocolCostContract.IsAuthorized(quote, consent.Request.Costs) || !SamePhysicalNetwork(row, quote)
            || row.Operation != "Mint" || row.ChainType != "Solana" || row.Stage != "Create" || row.PreparationGeneration <= 0
            || quote.LifecycleId != row.Id || quote.Stage != row.Stage || quote.PreparationGeneration != row.PreparationGeneration
            || quote.ActorUserId != command.Actor || quote.ActingTenantId != command.ActingTenantId
            || quote.PayerWalletId != command.ActorWalletId || quote.PayerAddress != command.ActorAddress
            || quote.ChainType != row.ChainType || quote.Network.ToString() != row.Network
            || (row.ProtocolIdentity is not null && row.ProtocolIdentity != quote.PhysicalNetworkIdentity)
            || quote.DraftHash != NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, command.Metadata)
            || authority.Kind is not ("DirectJwt" or "DirectApiKey") || authority.Action is not null || authority.WorkflowAuthorizationId is not null
            || authority.Credential.ActorUserId != command.Actor || (authority.Credential.TenantId ?? Guid.Empty) != command.ActingTenantId
            || authority.Wallet.Id != command.ActorWalletId || authority.Wallet.OwnerUserId != command.Actor
            || authority.Wallet.Address != command.ActorAddress || authority.Wallet.ChainType != row.ChainType) return false;
        var intent = NftLifecycleIntentFactory.Create(row, command, Enum.Parse<ChainNetwork>(row.Network),
            command.PlatformAuthorityAddress, command.ActingTenantId);
        return intent is not null && quote.QuotedIntentHash == NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId);
    }

    public static bool CanAppend(NftLifecycle before, NftLifecycle after)
    {
        if (!Eligible(before) || Current(before) is not null) return false;
        var old = Read(before);
        var next = Read(after);
        if (next.Count != old.Count + 1 || old.Where((item, index) =>
            JsonSerializer.Serialize(item) != JsonSerializer.Serialize(next[index])).Any()) return false;
        var request = next[^1].Request.Stage;
        if (request.RequestId == Guid.Empty || request.ExpectedVersion != before.Version || request.State != before.State
            || request.Stage != before.Stage || request.Generation != before.PreparationGeneration
            || old.Any(item => item.Request.Stage.RequestId == request.RequestId)
            || Retirements(before).Any(item => item.Request.Stage.RequestId == request.RequestId)) return false;
        var unchanged = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(after))!;
        unchanged.ReplacementCostConsentsJson = before.ReplacementCostConsentsJson;
        unchanged.UpdatedAt = before.UpdatedAt;
        return CanonicalJson.Hash(unchanged) == CanonicalJson.Hash(before)
            && Matches(after, JsonSerializer.Deserialize<NftLifecycleCommand>(after.RequestJson)!, next[^1]);
    }
}
