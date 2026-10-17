using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Advances an exchange source through durable preparation, exact-wire recovery and accounting.</summary>
/// <remarks>See Services/Reserve/AGENTS.md §durable-exchange.</remarks>
public sealed class ExchangeHoldSourceService(IExchangeHoldSourceStore store, IBlockchainProviderFactory providers,
    ExchangeOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Continues only the original authenticated API-key source and its immutable authority.</summary>
    public async Task<AZOAResult<ExchangeHoldSource>> ContinueAsync(string sourceId, SigningCredentialWitness credential,
        CancellationToken ct = default)
    {
        var source = await store.GetExchangeSourceAsync(sourceId, ct);
        var authority = source is null ? null : JsonSerializer.Deserialize<ExchangeParticipantAuthority>(source.AuthorityJson);
        if (source is null || authority is null || credential.Kind != "ApiKey"
            || credential.ActorUserId.ToString("N") != source.TenantUserId || credential.ApiKeyId?.ToString("N") != source.ApiKeyId
            || credential.TenantId is not null || CanonicalJson.Hash(credential) != CanonicalJson.Hash(authority.Tenant.Credential))
            return AZOAResult<ExchangeHoldSource>.Failure("The original authenticated exchange principal is required.");
        return await AdvanceAsync(source, true, ct);
    }

    /// <summary>Observes, rebroadcasts already-authorized wire, accounts for finality, or abandons a stale never-sent commitment.</summary>
    public async Task<AZOAResult<ExchangeHoldSource>> ReconcileAsync(string sourceId, CancellationToken ct = default)
    {
        var source = await store.GetExchangeSourceAsync(sourceId, ct);
        if (source is null) return AZOAResult<ExchangeHoldSource>.Failure("Exchange source was not found.");
        if (source.State is "Committed" or "Preparing" or "Prepared")
        {
            var staleBefore = _clock.GetUtcNow() - (options.UnsentSourceAbandonAfter > TimeSpan.Zero
                ? options.UnsentSourceAbandonAfter : TimeSpan.FromHours(1));
            if (source.UpdatedAt <= staleBefore) await store.AbandonUnsentExchangeAsync(source.Id, staleBefore, ct);
            return await ReloadAsync(source.Id, ct);
        }
        return await AdvanceAsync(source, false, ct);
    }

    private async Task<AZOAResult<ExchangeHoldSource>> AdvanceAsync(ExchangeHoldSource source, bool allowPreparation, CancellationToken ct)
    {
        if (source.State is "Completed" or "Compensated" or "Abandoned") return AZOAResult<ExchangeHoldSource>.Success(source);
        if (source.State is "Confirmed" or "ExpiredAbsent") return await ProjectAsync(source, ct);
        if (!allowPreparation && source.State != "Submitted") return AZOAResult<ExchangeHoldSource>.Success(source);
        var original = JsonSerializer.Deserialize<ExchangeHoldSourceIntent>(source.IntentJson)
            ?? throw new InvalidOperationException("Exchange source intent is unreadable.");
        var asset = original.Opening?.Asset ?? original.Units?.Asset
            ?? throw new InvalidOperationException("Exchange source names no settlement asset.");
        var provider = providers.GetProvider(asset.ChainType, asset.Network);
        if (!provider.TryGetModule<IExchangeTransactionModule>(out var module) || module is null)
            return AZOAResult<ExchangeHoldSource>.Failure("The exact exchange settlement capability is unavailable.");
        if (allowPreparation && source.State == "Admitted")
        {
            var committed = await store.CommitExchangeAsync(source.Id, ct);
            if (committed is null) return await ReloadAsync(source.Id, ct);
            source = committed;
        }
        if (allowPreparation && source.State is "Committed" or "Preparing")
        {
            var authorized = await store.BeginExchangePreparationAsync(source.Id, source.Version, ct);
            if (authorized is null) return await ReloadAsync(source.Id, ct);
            source = authorized;
            var intent = JsonSerializer.Deserialize<ExchangeWireIntent>(source.WireIntentJson!)!;
            var prepared = await module.PrepareExchangeAsync(intent, ct);
            if (prepared.IsError || prepared.Result is null) return AZOAResult<ExchangeHoldSource>.Failure(prepared.Message);
            if (await store.SaveExchangePreparedAsync(source.Id, source.Version, prepared.Result, ct) is null)
                return await ReloadAsync(source.Id, ct);
            source = await RequiredAsync(source.Id, ct);
        }
        if (allowPreparation && source.State == "Prepared")
        {
            var authorized = await store.AuthorizeExchangeSubmissionAsync(source.Id, source.Version, ct);
            if (authorized is null) return await ReloadAsync(source.Id, ct);
            source = authorized;
        }
        if (source.State != "Submitted") return AZOAResult<ExchangeHoldSource>.Success(source);
        var wire = JsonSerializer.Deserialize<PreparedExchangeWire>(source.PreparedWireJson!)
            ?? throw new InvalidOperationException("Submitted exchange has no original wire.");
        var observation = await module.ObserveExchangeAsync(wire, ct);
        if (observation.IsError || observation.Result is null) return AZOAResult<ExchangeHoldSource>.Failure(observation.Message);
        if (observation.Result.Verdict is "Confirmed" or "ExpiredAbsent")
        {
            if (await store.RecordExchangeEvidenceAsync(source.Id, source.Version, observation.Result, ct) is null)
                return await ReloadAsync(source.Id, ct);
            return await ProjectAsync(await RequiredAsync(source.Id, ct), ct);
        }
        if (observation.Result.Verdict != "Unknown")
            return AZOAResult<ExchangeHoldSource>.Failure("Exchange provider returned an unrecognized outcome.");
        try { await module.SubmitExchangeAsync(wire, ct); }
        catch (HttpRequestException)
        {
            // The authorized bytes stay saved; reconciliation rebroadcasts them or proves absence after the validity window.
            return AZOAResult<ExchangeHoldSource>.Success(await RequiredAsync(source.Id, ct), "Submission is pending reconciliation.");
        }
        return await ReloadAsync(source.Id, ct);
    }

    private async Task<AZOAResult<ExchangeHoldSource>> ProjectAsync(ExchangeHoldSource source, CancellationToken ct)
    {
        if (source.State == "Confirmed") await store.CompleteExchangeSettlementAsync(source.Id, ct);
        else if (source.State == "ExpiredAbsent") await store.CompensateExchangeAbsenceAsync(source.Id, ct);
        return await ReloadAsync(source.Id, ct);
    }

    private async Task<ExchangeHoldSource> RequiredAsync(string id, CancellationToken ct)
        => await store.GetExchangeSourceAsync(id, ct) ?? throw new InvalidOperationException("Admitted exchange source disappeared.");

    private async Task<AZOAResult<ExchangeHoldSource>> ReloadAsync(string id, CancellationToken ct)
        => AZOAResult<ExchangeHoldSource>.Success(await RequiredAsync(id, ct));
}
