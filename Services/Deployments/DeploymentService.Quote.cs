using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Deployments;

public sealed partial class DeploymentService
{
    /// <inheritdoc/>
    public Task<AZOAResult<NativeOperationIntent>> QuoteAsync(Guid actor, Guid? tenant, DeploymentQuoteRequest request, CancellationToken ct = default)
        => QuoteCoreAsync(actor, tenant, request, ct);

    private async Task<AZOAResult<NativeOperationIntent>> QuoteCoreAsync(Guid actor, Guid? tenant, DeploymentQuoteRequest request,
        CancellationToken ct, string? stableQuoteId = null)
    {
        if (request.Intent.Kind == "Recovery")
            return Fail<NativeOperationIntent>("Native recovery requires the original operation's linked recovery quote endpoint.");
        if (actor == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 200 || request.RequestKey.Any(char.IsControl))
            return Fail<NativeOperationIntent>("A bounded request key is required for a native quote.");
        var artifact = await store.GetArtifactAsync(request.ArtifactId, ct);
        if (artifact is null || artifact.OwnerUserId != actor.ToString("N") || artifact.TenantId != Tenant(tenant))
            return Fail<NativeOperationIntent>("Native artifact not found.");
        var payload = Read<DeploymentArtifactPayload>(artifact.PayloadJson);
        if (payload.Native is null || payload.Target.Kind != "NativeContract") return Fail<NativeOperationIntent>("A native artifact is required.");
        var intent = request.Intent with
        {
            Version = 1, OperationKey = DeploymentIdentity.Operation(actor, tenant, request.RequestKey),
            ArtifactHash = artifact.ArtifactHash, ChainType = payload.Target.ChainType, Network = payload.Target.Network,
            Budget = request.Intent.Budget with { QuoteId = "" }, Quote = null,
        };
        if (!ValidAuthority(intent, actor, tenant) || !Positive(intent.Budget.MaximumNetworkFee)
            || !Positive(intent.Budget.MaximumLockedRent) || !Positive(intent.Budget.MaximumValue))
            return Fail<NativeOperationIntent>("Quote requires exact caller authority and canonical fee, rent and value caps.");
        if (stableQuoteId is not null && await store.GetQuoteAsync(stableQuoteId, ct) is { } existing)
            return RestoreActionQuote(intent, existing, actor, tenant);
        var module = Module(payload.Target);
        if (module is null) return Fail<NativeOperationIntent>("Native operation module unavailable.");
        var quoted = await module.QuoteNativeOperationAsync(intent, ct);
        if (quoted.IsError || quoted.Result is null) return Fail<NativeOperationIntent>(quoted.Message);
        var quote = quoted.Result;
        if (string.IsNullOrWhiteSpace(quote.ProtocolIdentity) || !Positive(quote.NetworkFee) || !Positive(quote.LockedRent) || !Positive(quote.Value)
            || BigInteger.Parse(quote.NetworkFee, CultureInfo.InvariantCulture) > BigInteger.Parse(intent.Budget.MaximumNetworkFee, CultureInfo.InvariantCulture)
            || BigInteger.Parse(quote.LockedRent, CultureInfo.InvariantCulture) > BigInteger.Parse(intent.Budget.MaximumLockedRent, CultureInfo.InvariantCulture)
            || BigInteger.Parse(quote.Value, CultureInfo.InvariantCulture) > BigInteger.Parse(intent.Budget.MaximumValue, CultureInfo.InvariantCulture))
            return Fail<NativeOperationIntent>("Authoritative operation cost exceeds the requested authorization limits.");
        intent = intent with { ProtocolIdentity = quote.ProtocolIdentity };
        var intentHash = QuoteIntentHash(intent);
        quote = quote with { QuoteId = stableQuoteId ?? Guid.NewGuid().ToString("N"), IntentHash = intentHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        var candidate = new NativeQuote
        {
            Id = quote.QuoteId, ActorUserId = actor.ToString("N"), TenantId = Tenant(tenant), IntentHash = intentHash,
            QuoteJson = JsonSerializer.Serialize(quote), ExpiresAt = quote.ExpiresAt,
        };
        if (stableQuoteId is not null) return RestoreActionQuote(intent, await store.ClaimQuoteAsync(candidate, ct), actor, tenant);
        await store.AddQuoteAsync(candidate, ct);
        return AZOAResult<NativeOperationIntent>.Success(intent with { Budget = intent.Budget with { QuoteId = quote.QuoteId }, Quote = quote });
    }

    private static AZOAResult<NativeOperationIntent> RestoreActionQuote(NativeOperationIntent intent, NativeQuote saved, Guid actor, Guid? tenant)
    {
        var quote = Read<NativeOperationQuote>(saved.QuoteJson);
        intent = intent with { ProtocolIdentity = quote.ProtocolIdentity };
        if (saved.ActorUserId != actor.ToString("N") || saved.TenantId != Tenant(tenant) || saved.IntentHash != QuoteIntentHash(intent)
            || quote.IntentHash != saved.IntentHash || quote.QuoteId != saved.Id)
            return Fail<NativeOperationIntent>("Action operation quote belongs to another immutable request.");
        return AZOAResult<NativeOperationIntent>.Success(intent with { Quote = quote, Budget = intent.Budget with { QuoteId = quote.QuoteId } });
    }

    private static string QuoteIntentHash(NativeOperationIntent intent)
        => NativeOperationAuthorization.IntentHash(intent);

    private static bool ValidAuthority(NativeOperationIntent intent, Guid actor, Guid? tenant)
        => intent.Kind is "Deploy" or "Call" or "Account" or "Recovery" && intent.Authorities.Count > 0 && intent.Authorities.All(authority => !authority.SigningContext.IsPlatform
            && authority.SigningContext.UserId == actor && authority.SigningContext.WalletId != Guid.Empty
            && authority.SigningContext.ActingTenantId == (tenant ?? Guid.Empty)
            && authority.SigningContext.GrantorUserId == actor && authority.SigningContext.Scope == (intent.Kind switch
            {
                "Deploy" or "Recovery" => AzoaScopes.ContractDeploySign,
                "Call" => AzoaScopes.ContractCallSign,
                _ => AzoaScopes.SolanaAccountSign,
            }));

    private async Task<string?> CheckSigningAuthorityAsync(NativeOperationIntent intent, Guid actor, Guid? tenant, CancellationToken ct)
    {
        if (!ValidAuthority(intent, actor, tenant)) return "Native signing authority changed.";
        foreach (var authority in intent.Authorities)
        {
            var loaded = await wallets.GetByIdAsync(authority.SigningContext.WalletId, ct);
            if (loaded.IsError || loaded.Result is not { } wallet || wallet.UserId != actor || wallet.WalletType != WalletType.Platform
                || string.IsNullOrWhiteSpace(wallet.EncryptedPrivateKey) || !wallet.ChainType.Equals(intent.ChainType, StringComparison.OrdinalIgnoreCase)
                || !ChainAddressIdentity.Equivalent(intent.ChainType, wallet.Address, authority.Address))
                return "A signing wallet is no longer available to this caller.";
            var permission = await consent.EnsureAllowedAsync(authority.SigningContext, ct);
            if (permission.IsError || !permission.Result) return permission.Message;
        }
        return null;
    }
}
