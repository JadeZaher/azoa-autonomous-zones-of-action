using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public Task<ExchangeHoldSource?> GetExchangeSourceAsync(string sourceId, CancellationToken ct = default)
        => executor.QuerySingleAsync<ExchangeHoldSource>(SurrealQuery<ExchangeHoldSource>.Key(sourceId), ct);

    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> AdmitExchangeSourceAsync(ExchangeHoldSourceIntent intent,
        ExchangeParticipantAuthority authority, CancellationToken ct = default)
    {
        if (!ValidExchangeSource(intent, authority)) return null;
        var id = ExchangeHoldSourceIdentity.ForIntent(intent);
        var intentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(intent));
        var intentHash = CanonicalJson.HashCanonical(intentJson);
        var prior = await GetExchangeSourceAsync(id, ct);
        if (prior is not null) return SameExchangeRequest(prior, intent, intentJson) ? prior : null;
        var guards = ExchangeAuthorityFence.Create(authority, "exchange_source");
        if (guards is null) return null;
        var now = DateTimeOffset.UtcNow;
        var candidate = new ExchangeHoldSource { Id = id, TenantUserId = intent.TenantUserId.ToString("N"),
            ApiKeyId = intent.ApiKeyId.ToString("N"), ParticipantUserId = intent.ParticipantUserId.ToString("N"),
            RequestKey = intent.RequestKey, IntentHash = intentHash, IntentJson = intentJson,
            AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            CreatedAt = now, UpdatedAt = now };
        // raw: both actual authorities and immutable source admission commit before any hold or custody effect.
        var query = SurrealQuery.Combine(new[] { SurrealQuery.Of("BEGIN") }.Concat(guards)
            .Append(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(candidate), "exchange_source_create"))
            .Append(SurrealQuery.Of("COMMIT")).ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(query, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return candidate;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ExchangeAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            prior = await GetExchangeSourceAsync(id, ct);
            return prior is not null && SameExchangeRequest(prior, intent, intentJson) ? prior : null;
        }
    }

    /// <inheritdoc/>
    public async Task<ExchangeHoldSource?> CommitExchangeAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var intent = source is null ? null : JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        if (source is null || intent is null) return null;
        if (source.State != "Admitted") return source;
        if (intent.Opening is not null)
        {
            if (await CommitExchangeOpeningAsync(sourceId, ct) is null) return null;
            return await GetExchangeSourceAsync(sourceId, ct);
        }
        return await CommitExchangeUnitsAsync(source, ct);
    }

    /// <inheritdoc/>
    public async Task<HoldCommandReceipt?> CommitExchangeOpeningAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        var loaded = ReadExchangeSource(source);
        if (loaded is not { Intent.Opening: { } opening } context) return null;
        var economic = new HoldCommandAuthority(1, EconomicSource: new("Exchange", context.Source.Id, context.Source.IntentHash));
        return await CommitLocalCoreAsync(opening, economic, null, ct, context);
    }

    private static IReadOnlyList<SurrealQuery>? ExchangeOpeningFence(ExchangeOpeningCommit context,
        HoldCommandIntent opening, HoldCommandAuthority authority)
    {
        var source = context.Source;
        if (source.State != "Admitted" || source.Version is < 0 or long.MaxValue || source.HoldCommandId is not null
            || source.HoldRecordId is not null || context.Intent.Opening is null
            || CanonicalJson.Hash(opening) != CanonicalJson.Hash(context.Intent.Opening)
            || authority.Direct is not null || authority.Workflow is not null || authority.Sender is not null
            || authority.ReleaseMandateId is not null || authority.EconomicSource != new HoldEconomicSource("Exchange", source.Id, source.IntentHash)) return null;
        var actual = ExchangeAuthorityFence.Create(context.Authority, "exchange_opening");
        if (actual is null) return null;
        var statements = actual.ToList();
        var commandId = HoldCommandIdentity.Operation(opening);
        var holdId = HoldCommandIdentity.Hold(opening);
        // raw: source consumption and original reserve liability share the owning hold accounting transaction.
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<ExchangeHoldSource>(source.Id)
            .Where(row => row.Version == source.Version && row.State == "Admitted" && row.IntentHash == source.IntentHash
                && row.IntentJson == source.IntentJson && row.AuthorityJson == source.AuthorityJson
                && row.HoldCommandId == null && row.HoldRecordId == null)
            .Set(row => row.Version, source.Version + 1).Set(row => row.State, "Committed")
            .Set(row => row.HoldCommandId, commandId).Set(row => row.HoldRecordId, holdId)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), "exchange_committed", "_exchange_committed"));
        statements.Add(SurrealGeneratedStatementScope.RequireResult("_exchange_committed", Conflict));
        return statements;
    }

    /// <summary>True for the identical original request, or the same request carrying a revised or superseded release disclosure.</summary>
    private static bool SameExchangeRequest(ExchangeHoldSource prior, ExchangeHoldSourceIntent intent, string intentJson)
    {
        if (prior.IntentJson == intentJson) return true;
        if (prior.IntentHash != CanonicalJson.HashCanonical(prior.IntentJson)) return false;
        var saved = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(prior.IntentJson);
        return saved?.ReleaseConsent is { } current && intent.ReleaseConsent is { } offered
            && current.Terms == offered.Terms
            && CanonicalJson.Hash(saved with { ReleaseConsent = null }) == CanonicalJson.Hash(intent with { ReleaseConsent = null });
    }

    private static ExchangeOpeningCommit? ReadExchangeSource(ExchangeHoldSource? source)
    {
        if (source is null || source.IntentHash != CanonicalJson.HashCanonical(source.IntentJson)) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        var authority = JsonSerializer.Deserialize<ExchangeParticipantAuthority>(source.AuthorityJson);
        if (intent is null || authority is null || !ValidExchangeSource(intent, authority)
            || source.Id != ExchangeHoldSourceIdentity.ForIntent(intent)
            || source.TenantUserId != intent.TenantUserId.ToString("N") || source.ApiKeyId != intent.ApiKeyId.ToString("N")
            || source.ParticipantUserId != intent.ParticipantUserId.ToString("N") || source.RequestKey != intent.RequestKey) return null;
        return new(source, intent, authority);
    }

    private static bool ValidExchangeSource(ExchangeHoldSourceIntent intent, ExchangeParticipantAuthority authority)
    {
        if (intent.Version != 1 || intent.TenantUserId == Guid.Empty || intent.ApiKeyId == Guid.Empty
            || intent.ParticipantUserId == Guid.Empty || string.IsNullOrWhiteSpace(intent.RequestKey)
            || intent.RequestKey.Length > 200 || intent.RequestKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(intent.OriginalRequestJson) || intent.OriginalRequestJson.Length > 65536
            || string.IsNullOrWhiteSpace(intent.ProjectReference) || string.IsNullOrWhiteSpace(intent.TreasuryAddress)
            || string.IsNullOrWhiteSpace(intent.PlatformAssetId) || intent.PlatformAssetId != intent.PlatformAssetId.Trim()
            || !UnsignedIntegerText.TryParse(intent.PlatformQuantityBaseUnits, out var platformQuantity) || platformQuantity == 0
            || !UnsignedIntegerText.TryParse(intent.MaximumNetworkFeeBaseUnits, out var maximumFee) || maximumFee <= 0
            || authority.Tenant.Credential.ActorUserId != intent.TenantUserId || authority.Tenant.Credential.ApiKeyId != intent.ApiKeyId
            || authority.ParticipantUserId != intent.ParticipantUserId || authority.Wallet != intent.ParticipantWallet
            || (intent.Opening is null) == (intent.Units is null)) return false;
        HoldAssetIdentity asset;
        if (intent.Opening is { } opening)
        {
            if (opening is not { Version: 1, Kind: "Open", Definition: { FundingSource: "Reserve" } definition }
                || opening.ActorUserId != intent.TenantUserId || opening.OwnerUserId != intent.TenantUserId
                || opening.TenantId is not null || opening.CustodyTenantId != intent.TenantUserId
                || opening.RequestKey != "exchange:" + ExchangeHoldSourceIdentity.ForIntent(intent)
                || definition.BeneficiaryUserId != intent.ParticipantUserId || definition.Asset != opening.Asset
                || definition.AutoRelease != (intent.ReleaseConsent is not null)) return false;
            asset = opening.Asset;
        }
        else
        {
            var units = intent.Units!;
            if (units.Kind is not ("Issue" or "Return") || units.Asset is null || intent.ReleaseConsent is not null
                || !Enum.IsDefined(units.Asset.Network) || string.IsNullOrWhiteSpace(units.Asset.ProtocolIdentity)
                || string.IsNullOrWhiteSpace(units.Asset.AssetId) || units.Asset.AssetId != units.Asset.AssetId.Trim()
                || string.IsNullOrWhiteSpace(units.Asset.HoldingAddress) || units.Asset.AssetId == intent.PlatformAssetId
                || !UnsignedIntegerText.TryParse(units.QuantityBaseUnits, out var unitQuantity) || unitQuantity == 0
                || string.IsNullOrEmpty(units.ReservePartition)
                || units.ReservePartition != ReservePartitionCodec.Normalize(units.ReservePartition)
                || units.RecipientFundingBaseUnits is { } funding && !UnsignedIntegerText.TryParse(funding, out _)) return false;
            asset = units.Asset;
        }
        if (authority.Wallet.ChainType != asset.ChainType) return false;
        try
        {
            using var parsed = JsonDocument.Parse(intent.OriginalRequestJson);
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                && CanonicalJson.Canonicalize(parsed.RootElement) == intent.OriginalRequestJson;
        }
        catch (JsonException) { return false; }
    }

    private sealed record ExchangeOpeningCommit(ExchangeHoldSource Source, ExchangeHoldSourceIntent Intent,
        ExchangeParticipantAuthority Authority);
}
