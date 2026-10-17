using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Selects the requester authority fenced for new NFT effects; see Services/Users/AGENTS.md §NFT requester renewal.</summary>
public static class NftActorContinuationIdentity
{
    public static IReadOnlyList<NftActorContinuation> Read(NftLifecycle row)
        => JsonSerializer.Deserialize<NftActorContinuation[]>(row.ActorContinuationsJson)
            ?? throw new InvalidOperationException("NFT requester renewal history is unreadable.");

    /// <summary>The latest matching renewal, otherwise the original command authority; a non-matching renewal fails closed.</summary>
    public static SigningAdmissionAuthority? Effective(NftLifecycle row, NftLifecycleCommand command)
    {
        var latest = Read(row).LastOrDefault();
        return latest is null ? command.ActorAuthority : Matches(row, command, latest) ? latest.Authority : null;
    }

    /// <summary>States whose next transition fences requester authority before any new signature.</summary>
    public static bool Continuable(NftLifecycle row) => row.State is "Preparing" or "Prepared" or "AwaitingRecipientAcceptance"
        or "AwaitingCreatorDestruction" or "AwaitingRefundAuthorization" or "AwaitingRefundDestinationAcceptance";

    /// <summary>
    /// F-H12 owner takeover: the owner's own direct credential (no tenant) reclaiming a tenant-acting or workflow-admitted
    /// lifecycle whose original authority can no longer continue it. See Services/Users/AGENTS.md §NFT owner takeover.
    /// </summary>
    public static bool IsOwnerTakeover(NftLifecycleCommand command, NftActorContinuation continuation)
        => (continuation.Authority.Credential.TenantId ?? Guid.Empty) == Guid.Empty
            && (command.ActingTenantId != Guid.Empty || command.ActionAuthority is not null);

    /// <summary>True once a matching owner takeover is recorded; the original tenant or workflow authority is then locked out.</summary>
    public static bool TakenOver(NftLifecycle row, NftLifecycleCommand command)
        => Read(row).Any(continuation => IsOwnerTakeover(command, continuation) && Matches(row, command, continuation));

    public static bool Matches(NftLifecycle row, NftLifecycleCommand command, NftActorContinuation continuation)
    {
        var authority = continuation.Authority;
        var history = Read(row);
        var position = history.ToList().FindIndex(item => item.Request?.RequestId == continuation.Request?.RequestId);
        if (position < 0) position = history.Count;
        var takeover = IsOwnerTakeover(command, continuation);
        var sameRequester = !history.Take(position).Any(prior => IsOwnerTakeover(command, prior))
            && command.ActorAuthority?.Kind is "DirectJwt" or "DirectApiKey" && command.ActionAuthority is null
            && (authority.Credential.TenantId ?? Guid.Empty) == command.ActingTenantId;
        return (takeover || sameRequester)
            && continuation.Request?.RequestId is { } requestId && requestId != Guid.Empty
            && authority.Kind is "DirectJwt" or "DirectApiKey" && authority.Action is null && authority.WorkflowAuthorizationId is null
            && authority.Credential.ActorUserId == command.Actor
            && authority.Wallet.Id == command.ActorWalletId && authority.Wallet.OwnerUserId == command.Actor
            && authority.Wallet.Address == command.ActorAddress && authority.Wallet.ChainType == row.ChainType
            && authority.RequiredScopes.Count == 1 && authority.RequiredScopes[0] == NftSigningScope.ForOperation(row.Operation);
    }

    public static bool CanAppend(NftLifecycle before, NftLifecycle after)
    {
        if (!Continuable(before)) return false;
        var old = Read(before);
        var next = Read(after);
        if (next.Count != old.Count + 1 || old.Where((item, index) =>
            JsonSerializer.Serialize(item) != JsonSerializer.Serialize(next[index])).Any()) return false;
        var latest = next[^1];
        var request = latest.Request;
        if (request is null || request.ExpectedVersion != before.Version || request.State != before.State || request.Stage != before.Stage
            || request.Generation != before.PreparationGeneration || old.Any(item => item.Request.RequestId == request.RequestId)) return false;
        var unchanged = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(after))!;
        unchanged.ActorContinuationsJson = before.ActorContinuationsJson;
        unchanged.UpdatedAt = before.UpdatedAt;
        return CanonicalJson.Hash(unchanged) == CanonicalJson.Hash(before)
            && Matches(after, JsonSerializer.Deserialize<NftLifecycleCommand>(after.RequestJson)!, latest);
    }

    /// <summary>Display hint only: the fenced direct requester credential has lapsed; the transaction fence stays authoritative.</summary>
    public static bool NeedsRenewal(NftLifecycle row, NftLifecycleCommand command, DateTimeOffset now)
        => Continuable(row) && command.ActionAuthority is null
            && Effective(row, command) is { Kind: "DirectJwt" or "DirectApiKey" } authority
            && authority.Credential.ExpiresAt is { } expires && expires <= now;
}
