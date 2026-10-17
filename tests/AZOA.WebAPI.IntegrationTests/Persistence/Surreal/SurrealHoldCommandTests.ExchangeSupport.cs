using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    private string TreasuryId => ProjectTreasuryLedger.RecordIdFor("Algorand", "Devnet", _actor.ToString("N"), "project");

    /// <summary>The held-purchase fixture re-shaped as a direct delivery (<c>Issue</c>) or a sale (<c>Return</c>).</summary>
    private async Task<(ExchangeHoldSourceIntent Intent, ExchangeParticipantAuthority Authority)> ExchangeUnitsAsync(string kind,
        string key = "purchase")
    {
        var (intent, authority) = await ExchangeAsync(key);
        var asset = intent.Opening!.Asset;
        return (intent with { Opening = null, ReleaseConsent = null, Units = new(kind, asset, "400", "contributors") }, authority);
    }

    /// <summary>Admits, commits, prepares and authorizes first egress exactly as the coordinator would.</summary>
    private async Task<ExchangeHoldSource> SubmittedAsync(ExchangeHoldSourceIntent intent, ExchangeParticipantAuthority authority)
    {
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        source = (await _store.CommitExchangeAsync(source.Id))!;
        source = (await _store.BeginExchangePreparationAsync(source.Id, source.Version))!;
        source = (await _store.SaveExchangePreparedAsync(source.Id, source.Version, Wire(source)))!;
        return (await _store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version))!;
    }

    private static PreparedExchangeWire Wire(ExchangeHoldSource source)
        => new(JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!, "preparation", "group", ["tx"], ["c2lnbmVk"]);

    private static ExchangeWireObservation Verdict(ExchangeHoldSource source, string verdict)
    {
        var wire = JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson!)!;
        return new(verdict, wire.PreparationId, wire.GroupId, CanonicalJson.Hash(wire), wire.Intent.ProtocolIdentity,
            verdict == "Confirmed" ? "100" : null);
    }

    private async Task SeedTreasuryAsync(long credited = 0, long held = 0, string status = "Open")
    {
        var assets = new Dictionary<string, ProjectTreasuryAssetCounters>();
        if (credited > 0 || held > 0) assets["7"] = new(credited, BigInteger.Zero, held);
        await Save(new ProjectTreasuryLedger { Id = TreasuryId, Chain = "Algorand", Network = "Devnet",
            TenantId = "user:" + _actor.ToString("N"), ProjectReference = "project", TreasuryAddress = "treasury",
            DerivationVersion = "fixture", Status = status, Assets = ProjectTreasuryAssetCodec.Encode(assets) });
    }

    private async Task<ProjectTreasuryAssetCounters> TreasuryCounters()
    {
        var row = (await _executor.QuerySingleAsync<ProjectTreasuryLedger>(SurrealQuery<ProjectTreasuryLedger>.Key(TreasuryId)))!;
        return ProjectTreasuryAssetCodec.Decode(row.Assets, out _)!.GetValueOrDefault("7") ?? ProjectTreasuryAssetCounters.Empty;
    }

    private async Task SetContributorsAsync(ReservePartitionCounters counters)
    {
        var row = (await _executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(ReserveId)))!;
        await Execute(SurrealWriter.UpdateOnly<PlatformReserveLedger>(ReserveId).Where(value => value.Version == row.Version)
            .Set(value => value.Partitions, ReservePartitionCodec.Encode(new Dictionary<string, ReservePartitionCounters>
                { ["contributors"] = counters })).Build());
    }
}
