using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using SurrealForge.Client;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionLocalReceipt;
using Lifecycle = AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Attaches a confirmed grant to its requested holon with an atomic replayable receipt.</summary>
public sealed class ActionNftHolonLinkService(IServiceProvider services, IHolonStore holons,
    IActionLocalMutationStore mutations, ISurrealExecutor executor)
{
    public async Task<ActionExternalOperationObservation?> LinkAsync(ActionExternalOperationReference reference,
        ActionFrozenNodeInput input, Lifecycle confirmed, CancellationToken ct)
    {
        if (input.NodeType != ActionNodeType.Grant) return null;
        if (!ActionNodeConfig.TryDeserialize<GrantNodeConfig>(input.Config.GetRawText(), "Grant", out var config, out _))
            return Refuse(reference, "The frozen grant configuration is invalid.");
        if (config.HolonId is not { } holonId) return null;
        var hash = CanonicalJson.Hash(input);
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(confirmed.RequestJson);
        if (holonId == Guid.Empty || input.Version != 1 || reference.Version != 1 || reference.Domain != "nft"
            || reference.IntentHash != hash || reference.ActorUserId != input.ActorUserId
            || reference.ActingTenantId != input.ActingTenantId || reference.OperationId != confirmed.Id
            || reference.RequestKey != $"action:{input.RunId:N}:{input.NodeId:N}:nft"
            || confirmed.Id != NftLifecycleIdentity.ForRequest(input.ActorUserId, "Mint", reference.RequestKey, input.ActingTenantId ?? Guid.Empty)
            || confirmed.State != "Completed" || confirmed.Operation != "Mint"
            || string.IsNullOrWhiteSpace(confirmed.AssetId) || string.IsNullOrWhiteSpace(confirmed.FinalTransactionId) || string.IsNullOrWhiteSpace(confirmed.ChainType)
            || confirmed.ActorUserId != input.ActorUserId.ToString("N")
            || confirmed.RequestHash != NftLifecycleIdentity.Fingerprint(confirmed.RequestJson)
            || command?.ActionAuthority != new ActionEffectAuthority(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, hash))
            return Refuse(reference, "Confirmed grant identity does not match the frozen action.");

        var expected = new Receipt
        {
            Id = CanonicalJson.Hash(new { Domain = "nft-holon-link-v1", input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId })[..32],
            RunId = input.RunId.ToString("N"), NodeId = input.NodeId.ToString("N"), ActorId = input.ActorUserId.ToString("N"),
            TenantId = input.ActingTenantId?.ToString("N"), IntentHash = hash, CreatedAt = DateTimeOffset.UtcNow,
            OutputJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                holonId, confirmed.Id, confirmed.NftId, confirmed.AssetId, confirmed.ChainType,
                confirmed.Network, confirmed.ProtocolIdentity, confirmed.ContractAddress, confirmed.FinalTransactionId
            }))
        };
        var previous = await mutations.GetReceiptAsync(expected.Id, ct);
        if (previous is not null) return Replay(reference, previous, expected);

        var policyVersion = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
        var plan = new ActionLocalMutationPlan();
        var overlay = new ActionLocalHolonOverlay(holons, plan);
        var existing = await overlay.GetByIdAsync(holonId, ct);
        if (existing.IsError || existing.Result is null || existing.Result.UserId != input.ActorUserId || !existing.Result.IsActive)
            return await RecheckAsync("The requested holon is not currently available for this grant.");
        if (!string.IsNullOrWhiteSpace(existing.Result.TokenId)
            || !string.IsNullOrWhiteSpace(existing.Result.ChainId)
                && !string.Equals(existing.Result.ChainId, confirmed.ChainType, StringComparison.OrdinalIgnoreCase))
            return await RecheckAsync("The requested holon already has an asset binding; it cannot be overwritten by this grant.");
        var manager = ActivatorUtilities.CreateInstance<HolonManager>(services, overlay);
        var staged = await manager.UpdateAsync(holonId,
            new HolonUpdateModel { TokenId = confirmed.AssetId, ChainId = confirmed.ChainType }, input.ActorUserId);
        if (staged.IsError || !plan.IsWithinBounds())
            return await RecheckAsync(plan.Refusal ?? staged.Message ?? "The confirmed grant attachment requires recovery.");
        var committed = await mutations.CommitAsync(plan, expected, policyVersion, false, ct);
        return committed.IsError || committed.Result is null
            ? await RecheckAsync(committed.Message ?? "The confirmed grant attachment did not commit.")
            : Replay(reference, committed.Result, expected);

        async Task<ActionExternalOperationObservation?> RecheckAsync(string detail)
        {
            var raced = await mutations.GetReceiptAsync(expected.Id, ct);
            return raced is null ? Refuse(reference, detail) : Replay(reference, raced, expected);
        }
    }

    private static ActionExternalOperationObservation? Replay(ActionExternalOperationReference reference, Receipt saved, Receipt expected)
        => !saved.IsError && saved.Id == expected.Id && saved.RunId == expected.RunId && saved.NodeId == expected.NodeId
            && saved.ActorId == expected.ActorId && saved.TenantId == expected.TenantId && saved.IntentHash == expected.IntentHash
            && saved.OutputJson == expected.OutputJson ? null : Refuse(reference, "The grant attachment receipt conflicts with this operation.");

    private static ActionExternalOperationObservation Refuse(ActionExternalOperationReference reference, string detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
