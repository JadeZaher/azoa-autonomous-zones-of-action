using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
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
        if (prior is not null) return prior.IntentHash == intentHash && prior.IntentJson == intentJson ? prior : null;
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
            return prior is not null && prior.IntentHash == intentHash && prior.IntentJson == intentJson ? prior : null;
        }
    }

    /// <inheritdoc/>
    public async Task<HoldCommandReceipt?> CommitExchangeOpeningAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await GetExchangeSourceAsync(sourceId, ct);
        if (source is null || source.IntentHash != CanonicalJson.HashCanonical(source.IntentJson)) return null;
        var intent = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson);
        var authority = JsonSerializer.Deserialize<ExchangeParticipantAuthority>(source.AuthorityJson);
        if (intent is null || authority is null || !ValidExchangeSource(intent, authority)
            || source.Id != ExchangeHoldSourceIdentity.ForIntent(intent)
            || source.TenantUserId != intent.TenantUserId.ToString("N") || source.ApiKeyId != intent.ApiKeyId.ToString("N")
            || source.ParticipantUserId != intent.ParticipantUserId.ToString("N") || source.RequestKey != intent.RequestKey) return null;
        var economic = new HoldCommandAuthority(1, EconomicSource: new("Exchange", source.Id, source.IntentHash));
        return await CommitLocalCoreAsync(intent.Opening, economic, null, ct, new(source, intent, authority));
    }

    private static IReadOnlyList<SurrealQuery>? ExchangeOpeningFence(ExchangeOpeningCommit context,
        HoldCommandIntent opening, HoldCommandAuthority authority)
    {
        var source = context.Source;
        if (source.State != "Admitted" || source.Version is < 0 or long.MaxValue || source.HoldCommandId is not null
            || source.HoldRecordId is not null || CanonicalJson.Hash(opening) != CanonicalJson.Hash(context.Intent.Opening)
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
        statements.Add(SurrealQuery.Of("IF $_exchange_committed = NONE OR $_exchange_committed = [] { THROW 'HOLD_COMMAND_CHANGED' }"));
        return statements;
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
            || intent.Opening is not { Version: 1, Kind: "Open", Definition: { FundingSource: "Reserve" } definition } opening
            || opening.ActorUserId != intent.TenantUserId || opening.OwnerUserId != intent.TenantUserId
            || opening.TenantId is not null || opening.CustodyTenantId != intent.TenantUserId
            || opening.RequestKey != "exchange:" + ExchangeHoldSourceIdentity.ForIntent(intent)
            || definition.BeneficiaryUserId != intent.ParticipantUserId || definition.Asset != opening.Asset
            || definition.AutoRelease != (intent.ReleaseConsent is not null)
            || authority.Tenant.Credential.ActorUserId != intent.TenantUserId || authority.Tenant.Credential.ApiKeyId != intent.ApiKeyId
            || authority.ParticipantUserId != intent.ParticipantUserId || authority.Wallet != intent.ParticipantWallet
            || authority.Wallet.ChainType != opening.Asset.ChainType) return false;
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
