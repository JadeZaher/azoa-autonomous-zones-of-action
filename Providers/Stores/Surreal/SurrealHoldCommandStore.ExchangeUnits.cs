using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Direct-delivery purchases and sales: reserve/treasury commitment, finalized settlement and proven-absence return.</summary>
/// <remarks>See Providers/Stores/Surreal/AGENTS.md §exchange-units.</remarks>
public sealed partial class SurrealHoldCommandStore
{
    private async Task<ExchangeHoldSource?> CommitExchangeUnitsAsync(ExchangeHoldSource stored, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await CommitExchangeUnitsAttemptAsync(stored.Id, ct);
            if (!outcome.Contended || attempt >= MaxReplans) return outcome.Result;
        }
    }

    /// <summary>One commitment pass from a fresh read; contention means a counter row moved while the source stayed Admitted.</summary>
    private async Task<Attempt<ExchangeHoldSource>> CommitExchangeUnitsAttemptAsync(string sourceId, CancellationToken ct)
    {
        var stored = await GetExchangeSourceAsync(sourceId, ct);
        if (stored is not null && stored.State != "Admitted") return new(stored, false);
        var context = ReadExchangeSource(stored);
        if (context is not { Intent.Units: { } units } || stored!.Version is < 0 or long.MaxValue
            || stored.HoldCommandId is not null || stored.HoldRecordId is not null) return default;
        var source = context.Source; var intent = context.Intent;
        var guards = ExchangeAuthorityFence.Create(context.Authority, "exchange_units");
        var reserve = await ExchangeReserveAsync(units.Asset, ct);
        if (guards is null || reserve is null
            || ReservePartitionScope.Refuse(reserve, ReserveDrawAuthority.ForTenant(intent.TenantUserId), reserve.AssetId, options) is not null) return default;
        var now = DateTimeOffset.UtcNow;
        // raw: actual two-party authority, source consumption and the scarce reserve or treasury commitment commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == source.Version && row.State == "Admitted" && row.IntentHash == source.IntentHash
                && row.IntentJson == source.IntentJson && row.AuthorityJson == source.AuthorityJson
                && row.HoldCommandId == null && row.HoldRecordId == null)
            .Set(row => row.Version, source.Version + 1).Set(row => row.State, "Committed").Set(row => row.UpdatedAt, now).Build(),
            "exchange_units_committed", "_exchange_units_committed"));
        statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_units_committed", Conflict));
        if (units.Kind == "Issue")
        {
            var planned = HoldReserveMutationPlan.Create(reserve, units.ReservePartition, ReserveDrawKind.Hold,
                units.QuantityBaseUnits, units.Asset.ProtocolIdentity);
            if (planned.IsError || planned.Result is not { } mutation) return default;
            statements.AddRange(ReserveWrite(reserve, mutation, now, "exchange_units_reserve"));
        }
        else
        {
            if (HoldReserveMutationPlan.Create(reserve, units.ReservePartition, ReserveDrawKind.Return,
                units.QuantityBaseUnits, units.Asset.ProtocolIdentity).IsError) return default;
            var treasury = await ExchangeTreasuryAsync(intent, units.Asset, ct);
            if (treasury is null || treasury.Status != "Open") return default;
            var write = TreasuryMovement(treasury, intent.PlatformAssetId, ProjectTreasuryMovementKind.Hold, intent.PlatformQuantityBaseUnits, now, "exchange_units_treasury");
            if (write is null) return default;
            statements.AddRange(write);
        }
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        var contended = false;
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(Conflict, StringComparison.Ordinal))
        {
            contended = true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ExchangeAuthorityFence.FailureMarker, StringComparison.Ordinal)) { }
        var current = await GetExchangeSourceAsync(source.Id, ct);
        return current is not null && current.IntentHash == source.IntentHash && current.State != "Admitted"
            ? new(current, false) : new(null, contended && current?.State == "Admitted");
    }

    private async Task<TerminalPlan?> PlanUnitsSettlementAsync(ExchangeHoldSource source,
        ExchangeHoldSourceIntent intent, ExchangeUnitsCommitment units, ExchangeWireObservation evidence, CancellationToken ct)
    {
        var reserve = await ExchangeReserveAsync(units.Asset, ct);
        var treasury = await ExchangeTreasuryAsync(intent, units.Asset, ct);
        if (reserve is null || treasury is null) return null;
        var now = DateTimeOffset.UtcNow;
        var statements = SourceTransition(source, "Confirmed", "Completed", now, "exchange_units_final");
        string reserveMovement;
        string treasuryMovement;
        if (units.Kind == "Issue")
        {
            var planned = HoldReserveMutationPlan.Create(reserve, units.ReservePartition, ReserveDrawKind.ReleaseHold,
                units.QuantityBaseUnits, units.Asset.ProtocolIdentity);
            var credit = TreasuryMovement(treasury, intent.PlatformAssetId, ProjectTreasuryMovementKind.Credit, intent.PlatformQuantityBaseUnits, now, "exchange_units_final_treasury");
            if (planned.IsError || planned.Result is not { } mutation || credit is null) return null;
            statements.AddRange(ReserveWrite(reserve, mutation, now, "exchange_units_final_reserve"));
            statements.AddRange(credit);
            reserveMovement = $"ReleaseHold:{units.QuantityBaseUnits}";
            treasuryMovement = $"Credit:{intent.PlatformQuantityBaseUnits}";
        }
        else
        {
            var returned = ReturnableUnits(reserve, units);
            var release = TreasuryMovement(treasury, intent.PlatformAssetId, ProjectTreasuryMovementKind.ReleaseHold, intent.PlatformQuantityBaseUnits, now, "exchange_units_final_treasury");
            if (returned is null || release is null) return null;
            if (returned.Value > BigInteger.Zero)
            {
                var planned = HoldReserveMutationPlan.Create(reserve, units.ReservePartition, ReserveDrawKind.Return,
                    returned.Value.ToString(CultureInfo.InvariantCulture), units.Asset.ProtocolIdentity);
                if (planned.IsError || planned.Result is not { } mutation) return null;
                statements.AddRange(ReserveWrite(reserve, mutation, now, "exchange_units_final_reserve"));
            }
            statements.AddRange(release);
            reserveMovement = $"Return:{returned.Value.ToString(CultureInfo.InvariantCulture)}";
            treasuryMovement = $"ReleaseHold:{intent.PlatformQuantityBaseUnits}";
        }
        return new(statements, new ExchangeHoldSettlementReceipt { Id = source.Id, SourceIntentHash = source.IntentHash,
            PreparedHash = evidence.PreparedHash, EvidenceJson = source.SettlementEvidenceJson!, TreasuryRecordId = treasury.Id,
            ReserveRecordId = reserve.Id, MovementJson = JsonSerializer.Serialize(new { Reserve = reserveMovement, Treasury = treasuryMovement }),
            CreatedAt = now });
    }

    /// <summary>Plans the exact inverse of an unsettled direct commitment; returns null when its counters no longer prove it.</summary>
    private async Task<IReadOnlyList<SurrealQuery>?> UnitsReleaseAsync(ExchangeHoldSourceIntent intent, ExchangeUnitsCommitment units,
        DateTimeOffset now, ExchangeHoldSettlementReceipt receipt, CancellationToken ct)
    {
        if (units.Kind == "Issue")
        {
            var reserve = await ExchangeReserveAsync(units.Asset, ct);
            if (reserve is null) return null;
            var planned = HoldReserveMutationPlan.Create(reserve, units.ReservePartition, ReserveDrawKind.CancelHold,
                units.QuantityBaseUnits, units.Asset.ProtocolIdentity);
            if (planned.IsError || planned.Result is not { } mutation) return null;
            receipt.ReserveRecordId = reserve.Id;
            receipt.MovementJson = JsonSerializer.Serialize(new { Reserve = $"CancelHold:{units.QuantityBaseUnits}" });
            return ReserveWrite(reserve, mutation, now, "exchange_units_release_reserve");
        }
        var treasury = await ExchangeTreasuryAsync(intent, units.Asset, ct);
        var cancel = treasury is null ? null
            : TreasuryMovement(treasury, intent.PlatformAssetId, ProjectTreasuryMovementKind.CancelHold, intent.PlatformQuantityBaseUnits, now, "exchange_units_release_treasury");
        if (treasury is null || cancel is null) return null;
        receipt.TreasuryRecordId = treasury.Id;
        receipt.MovementJson = JsonSerializer.Serialize(new { Treasury = $"CancelHold:{intent.PlatformQuantityBaseUnits}" });
        return cancel;
    }

    /// <summary>Sale units arriving beyond the partition's outstanding issue are not budget it ever promised.</summary>
    private static BigInteger? ReturnableUnits(PlatformReserveLedger reserve, ExchangeUnitsCommitment units)
    {
        var partitions = ReservePartitionCodec.Decode(reserve.Partitions, out _);
        if (partitions is null || !partitions.TryGetValue(units.ReservePartition, out var counters)
            || !UnsignedIntegerText.TryParse(units.QuantityBaseUnits, out var quantity)) return null;
        var outstanding = BigInteger.Max(BigInteger.Zero, counters.Issued - counters.Returned);
        return BigInteger.Min(quantity, outstanding);
    }

    // raw: opens the source's terminal-accounting transaction; the receipt helper commits it with the counter writes.
    private static List<SurrealQuery> SourceTransition(ExchangeHoldSource source, string from, string to, DateTimeOffset now,
        string scope, string? recordEvidence = null, DateTimeOffset? staleBefore = null)
    {
        var update = staleBefore is { } stale
            ? SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
                .Where(row => row.Version == source.Version && row.State == from && row.IntentJson == source.IntentJson
                    && row.PreparedWireJson == source.PreparedWireJson && row.SettlementEvidenceJson == source.SettlementEvidenceJson
                    && row.UpdatedAt <= stale)
            : SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
                .Where(row => row.Version == source.Version && row.State == from && row.IntentJson == source.IntentJson
                    && row.PreparedWireJson == source.PreparedWireJson && row.SettlementEvidenceJson == source.SettlementEvidenceJson);
        update = update.Set(row => row.State, to).Set(row => row.Version, source.Version + 1).Set(row => row.UpdatedAt, now);
        if (recordEvidence is not null) update = update.Set(row => row.SettlementEvidenceJson, recordEvidence);
        return
        [
            SurrealQuery.Of("BEGIN"),
            SurrealGeneratedStatementScope.Assign(update.Build(), scope, "_" + scope),
            SurrealGeneratedStatementScope.RequireResult("_" + scope, Conflict),
        ];
    }

    private static IReadOnlyList<SurrealQuery> ReserveWrite(PlatformReserveLedger reserve, HoldReserveMutationPlan mutation,
        DateTimeOffset now, string scope)
        =>
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserve.Id)
                .Where(row => row.Version == reserve.Version && row.Chain == reserve.Chain && row.Network == reserve.Network
                    && row.ProtocolIdentity == reserve.ProtocolIdentity && row.AssetId == reserve.AssetId
                    && row.ReserveAddress == reserve.ReserveAddress && row.TenantId == reserve.TenantId
                    && row.TotalSupply == reserve.TotalSupply && row.Decimals == reserve.Decimals && row.UnitName == reserve.UnitName)
                .Set(row => row.Version, reserve.Version + 1).Set(row => row.Partitions, mutation.Partitions)
                .Set(row => row.UpdatedAt, now).Build(), scope, "_" + scope),
            SurrealGeneratedStatementScope.RequireResult("_" + scope, Conflict),
        ];

    private static IReadOnlyList<SurrealQuery>? TreasuryMovement(ProjectTreasuryLedger treasury, string assetId,
        ProjectTreasuryMovementKind kind, string quantity, DateTimeOffset now, string scope)
    {
        var assets = ProjectTreasuryAssetCodec.Decode(treasury.Assets, out _);
        if (assets is null || treasury.Version == long.MaxValue || !UnsignedIntegerText.TryParse(quantity, out var amount)) return null;
        var counters = assets.GetValueOrDefault(assetId) ?? ProjectTreasuryAssetCounters.Empty;
        if (!counters.TryApply(kind, amount, assetId, out var next, out _)) return null;
        assets[assetId] = next;
        return
        [
            SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ProjectTreasuryLedger>(treasury.Id)
                .Where(row => row.Version == treasury.Version && row.TreasuryAddress == treasury.TreasuryAddress
                    && row.Status == treasury.Status && row.TenantId == treasury.TenantId && row.ProjectReference == treasury.ProjectReference)
                .Set(row => row.Assets, ProjectTreasuryAssetCodec.Encode(assets)).Set(row => row.Version, treasury.Version + 1)
                .Set(row => row.UpdatedAt, now).Build(), scope, "_" + scope),
            SurrealGeneratedStatementScope.RequireResult("_" + scope, Conflict),
        ];
    }

    private async Task<PlatformReserveLedger?> ExchangeReserveAsync(HoldAssetIdentity asset, CancellationToken ct)
    {
        var reserve = await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(
            PlatformReserveLedger.RecordIdFor(asset.ChainType, asset.Network.ToString(), asset.AssetId)), ct);
        return reserve is not null && reserve.Chain == asset.ChainType && reserve.Network == asset.Network.ToString()
            && reserve.AssetId == asset.AssetId && reserve.ReserveAddress == asset.HoldingAddress
            && reserve.ProtocolIdentity == asset.ProtocolIdentity && reserve.Version is >= 0 and < long.MaxValue ? reserve : null;
    }

    private async Task<ProjectTreasuryLedger?> ExchangeTreasuryAsync(ExchangeHoldSourceIntent intent, HoldAssetIdentity asset, CancellationToken ct)
    {
        var treasury = await executor.QuerySingleAsync<ProjectTreasuryLedger>(SurrealQuery<ProjectTreasuryLedger>.Key(
            ProjectTreasuryLedger.RecordIdFor(asset.ChainType, asset.Network.ToString(), intent.TenantUserId.ToString("N"), intent.ProjectReference)), ct);
        return treasury is not null && string.Equals(treasury.Chain, asset.ChainType, StringComparison.OrdinalIgnoreCase)
            && treasury.Network == asset.Network.ToString()
            && SurrealRecordGuid.ParseOptional(treasury.TenantId) == intent.TenantUserId && treasury.ProjectReference == intent.ProjectReference
            && treasury.TreasuryAddress == intent.TreasuryAddress ? treasury : null;
    }

}
