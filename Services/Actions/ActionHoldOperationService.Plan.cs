using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Actions.Handlers;
using ActionNodeState = AZOA.WebAPI.Models.Actions.ActionNodeState;

namespace AZOA.WebAPI.Services.Actions;

public sealed partial class ActionHoldOperationService
{
    private sealed record Plan(HoldCommandIntent Intent, Guid? RecipientWalletId, HoldWorkflowMandateSelection? Mandate);

    private static string? ValidationError(ActionFrozenNodeInput input)
    {
        if (input.NodeType == ActionNodeType.Hold)
        {
            if (!Config<HoldNodeConfig>(input, out var open) || string.IsNullOrWhiteSpace(open.ChainType)
                || string.IsNullOrWhiteSpace(open.AssetId) || string.IsNullOrWhiteSpace(open.ReservePartition)
                || !UnsignedIntegerText.TryParse(open.Amount, out var amount) || amount <= 0
                || !ValidSelection(open.Network, open.BeneficiaryUserId, open.RecipientWalletId, open.AutoRelease, open.Mandate, open.ReleaseNotBefore))
                return "Hold requires a positive canonical amount, asset, reserve partition and explicit automatic-release limits.";
            return null;
        }
        string? chain; string? record; string? human; string? named; ChainNetwork? network;
        if (input.NodeType == ActionNodeType.Release && Config<ReleaseNodeConfig>(input, out var release))
            (chain, record, human, named, network) = (release.ChainType, release.HoldRecordId, release.HoldId, release.HoldNodeName, release.Network);
        else if (input.NodeType == ActionNodeType.HoldRefund && Config<HoldRefundNodeConfig>(input, out var refund))
            (chain, record, human, named, network) = (refund.ChainType, refund.HoldRecordId, refund.HoldId, refund.HoldNodeName, refund.Network);
        else if (input.NodeType == ActionNodeType.ReleaseIntoHold && Config<ReleaseIntoHoldNodeConfig>(input, out var child))
        {
            (chain, record, human, named, network) = (child.ChainType, child.HoldRecordId, child.HoldId, child.HoldNodeName, child.Network);
            if (!ValidSelection(child.Network, child.NextBeneficiaryUserId, child.RecipientWalletId, child.AutoRelease, child.Mandate, child.ReleaseNotBefore))
                return "Automatic child release requires an explicit recipient wallet and independent release limits.";
        }
        else return "The frozen Hold configuration is invalid.";
        return string.IsNullOrWhiteSpace(chain) || new[] { record, human, named }.Count(value => !string.IsNullOrWhiteSpace(value)) != 1
            || (network.HasValue && !Enum.IsDefined(network.Value)) || (!string.IsNullOrWhiteSpace(human) && network is null)
            ? "Hold settlement requires its chain and exactly one original source." : null;
    }

    private static bool ValidSelection(ChainNetwork? network, Guid? beneficiary, Guid? wallet, bool automatic,
        HoldWorkflowMandateSelection? selection, DateTimeOffset? notBefore)
    {
        if (network is null || !Enum.IsDefined(network.Value) || beneficiary == Guid.Empty || wallet == Guid.Empty) return false;
        if (!automatic) return selection is null;
        return wallet.HasValue && selection?.MaximumCosts is { } caps
            && selection.ExpiresAt != default && (!notBefore.HasValue || selection.ExpiresAt > notBefore.Value)
            && !string.IsNullOrWhiteSpace(caps.PayerAddress) && !string.IsNullOrWhiteSpace(caps.RefundRecipientAddress)
            && UnsignedIntegerText.TryParse(caps.NetworkFeeBaseUnits, out _)
            && UnsignedIntegerText.TryParse(caps.LockedBalanceBaseUnits, out _)
            && UnsignedIntegerText.TryParse(caps.NonrefundableProtocolCostBaseUnits, out _);
    }

    private async Task<(Plan? Plan, string? Error)> PlanAsync(ActionFrozenNodeInput input,
        ActionExternalOperationReference reference, CancellationToken ct)
    {
        if (input.NodeType == ActionNodeType.Hold)
        {
            if (!Config<HoldNodeConfig>(input, out var open)) return (null, "The frozen Hold config is unreadable.");
            var provider = providers.GetProvider(open.ChainType!, open.Network!.Value);
            var ledgerResult = await reserve.GetAsync(PlatformReserveLedger.RecordIdFor(provider.ChainType, provider.ActiveNetwork.ToString(), open.AssetId!), ct);
            if (ledgerResult.IsError || ledgerResult.Result is not { } ledger || string.IsNullOrWhiteSpace(ledger.ProtocolIdentity)
                || !Enum.TryParse<ChainNetwork>(ledger.Network, out var network) || !Enum.IsDefined(network)
                || network != provider.ActiveNetwork || ledger.Chain != provider.ChainType || ledger.AssetId != open.AssetId)
                return (null, "The exact reserve network and issuance evidence are unavailable.");
            var asset = new HoldAssetIdentity(ledger.Chain, network, ledger.ProtocolIdentity, ledger.AssetId, ledger.ReserveAddress);
            var holdId = string.IsNullOrWhiteSpace(open.HoldId) ? $"run:{input.RunId:N}:node:{input.NodeId:N}" : open.HoldId.Trim();
            var definition = new HoldDefinition(asset, open.Amount!, "Reserve", open.BeneficiaryUserId ?? input.ActorUserId,
                null, open.ReservePartition, open.ReleaseNotBefore, open.AutoRelease, open.Purpose);
            return (new(new(1, reference.RequestKey, "Open", holdId, input.ActorUserId, input.ActorUserId,
                input.ActingTenantId, input.ActingTenantId, asset, definition), open.RecipientWalletId, open.Mandate), null);
        }
        string? chain; ChainNetwork? selected; string? recordId; string? holdIdInput; string? sourceName;
        Guid? recipient; string? reason; ReleaseIntoHoldNodeConfig? chaining = null;
        if (input.NodeType == ActionNodeType.Release && Config<ReleaseNodeConfig>(input, out var release))
            (chain, selected, recordId, holdIdInput, sourceName, recipient, reason) =
                (release.ChainType, release.Network, release.HoldRecordId, release.HoldId, release.HoldNodeName, release.RecipientWalletId, release.Reason);
        else if (input.NodeType == ActionNodeType.HoldRefund && Config<HoldRefundNodeConfig>(input, out var refund))
            (chain, selected, recordId, holdIdInput, sourceName, recipient, reason) =
                (refund.ChainType, refund.Network, refund.HoldRecordId, refund.HoldId, refund.HoldNodeName, refund.RecipientWalletId, refund.Reason);
        else if (input.NodeType == ActionNodeType.ReleaseIntoHold && Config<ReleaseIntoHoldNodeConfig>(input, out var child))
        {
            chaining = child;
            (chain, selected, recordId, holdIdInput, sourceName, recipient, reason) =
                (child.ChainType, child.Network, child.HoldRecordId, child.HoldId, child.HoldNodeName, child.RecipientWalletId, child.Purpose);
        }
        else return (null, "The frozen Hold settlement configuration is invalid.");
        if (string.IsNullOrWhiteSpace(chain) || new[] { recordId, holdIdInput, sourceName }.Count(value => !string.IsNullOrWhiteSpace(value)) != 1)
            return (null, "Exactly one original Hold source is required.");
        HoldLedger? hold;
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            var run = await runs.GetByIdAsync(input.RunId, ct);
            if (run.IsError || run.Result?.DefinitionSnapshotJson is null) return (null, "The original frozen graph is unavailable.");
            var nodes = ActionRunSnapshot.Read(run.Result).Nodes.Where(node => node.Name == sourceName
                && node.NodeType is ActionNodeType.Hold or ActionNodeType.ReleaseIntoHold).ToArray();
            if (nodes.Length != 1 || !input.AllExecutions.TryGetValue(nodes[0].Id, out var predecessor)
                || predecessor.RunId != input.RunId || predecessor.NodeId != nodes[0].Id
                || predecessor.State != ActionNodeState.Succeeded || predecessor.Output is null)
                return (null, "The original named Hold source has not succeeded in this frozen execution.");
            using var output = JsonDocument.Parse(predecessor.Output);
            if (!output.RootElement.TryGetProperty("OperationId", out var operationId) || operationId.ValueKind != JsonValueKind.String)
                return (null, "The source Hold output lacks its durable accounting receipt.");
            var originalKey = $"action:{input.RunId:N}:{nodes[0].Id:N}:hold";
            if (operationId.GetString() != HoldCommandIdentity.Operation(input.ActorUserId, input.ActingTenantId, originalKey))
                return (null, "The named source output belongs to another custody command.");
            var receipt = await holds.GetReceiptAsync(operationId.GetString()!, ct);
            if (receipt is null || receipt.ActorUserId != input.ActorUserId.ToString("N") || receipt.TenantId != input.ActingTenantId?.ToString("N"))
                return (null, "The source Hold accounting receipt is unavailable to this actor.");
            var original = JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson);
            if (original?.State != "Completed" || original.OperationId != operationId.GetString()
                || receipt.Id != operationId.GetString()) return (null, "The source Hold accounting did not complete for this exact command.");
            recordId = nodes[0].NodeType == ActionNodeType.ReleaseIntoHold ? original.NextHoldRecordId : original.HoldRecordId;
            if (recordId is null) return (null, "The source receipt has no resulting Hold liability.");
            hold = await holds.GetHoldAsync(recordId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(recordId)) hold = await holds.GetHoldAsync(recordId, ct);
        else
        {
            var provider = providers.GetProvider(chain, selected!.Value);
            hold = await holds.FindOwnedHoldAsync(input.ActorUserId, input.ActingTenantId, provider.ChainType, provider.ActiveNetwork, holdIdInput!.Trim(), ct);
        }
        if (hold?.DefinitionJson is null || hold.OwnerUserId != input.ActorUserId.ToString("N")
            || SurrealRecordGuid.ParseOptional(hold.TenantId) != input.ActingTenantId
            || !string.Equals(hold.Chain, chain, StringComparison.OrdinalIgnoreCase)
            || !Enum.TryParse<ChainNetwork>(hold.Network, out var sourceNetwork) || (selected is { } requested && sourceNetwork != requested))
            return (null, "The exact original owned Hold is unavailable.");
        var storedDefinition = JsonSerializer.Deserialize<HoldDefinition>(hold.DefinitionJson)!;
        var kind = input.NodeType == ActionNodeType.Release ? "Release" : input.NodeType == ActionNodeType.HoldRefund ? "Refund" : "ReleaseIntoHold";
        var command = new HoldCommandIntent(1, reference.RequestKey, kind, hold.HoldId, input.ActorUserId,
            SurrealRecordGuid.Parse(hold.OwnerUserId), input.ActingTenantId, SurrealRecordGuid.ParseOptional(hold.TenantId), storedDefinition.Asset,
            NextHoldId: chaining is null ? null : string.IsNullOrWhiteSpace(chaining.NextHoldId)
                ? $"run:{input.RunId:N}:node:{input.NodeId:N}:next" : chaining.NextHoldId.Trim(),
            NextBeneficiaryUserId: chaining is null ? null : chaining.NextBeneficiaryUserId ?? input.ActorUserId,
            NextReleaseNotBefore: chaining?.ReleaseNotBefore, NextAutoRelease: chaining?.AutoRelease ?? false, Reason: reason);
        if (chaining?.AutoRelease == true && (chaining.Mandate is null || recipient is null))
            return (null, "Automatic child release requires explicit frozen deadline, recipient wallet and independent cost caps.");
        return (new(command, recipient, chaining?.Mandate), null);
    }

    private static bool Config<T>(ActionFrozenNodeInput input, out T value) where T : new()
    {
        if (input.Config.ValueKind != JsonValueKind.Object) { value = new(); return false; }
        return ActionNodeConfig.TryDeserialize(input.Config.GetRawText(), input.NodeType.ToString(), out value, out _);
    }
}
