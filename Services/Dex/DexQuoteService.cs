using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Signing;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Issues immutable owner-bound quotes with server-selected fee policy and provider-verified asset evidence.</summary>
public sealed class DexQuoteService(IDexQuoteStore quotes, IWalletStore wallets, INodeFeeScheduleManager fees,
    SigningAdmissionAuthorityFactory authority, DexModuleResolver modules, IConfiguration configuration)
{
    public async Task<AZOAResult<DexQuoteAvailability>> QuoteAsync(SigningCredentialWitness credential,
        DexQuoteRequest request, CancellationToken ct = default)
    {
        if (request is null || request.WalletId == Guid.Empty || request.Network is null || request.InputAsset is null ||
            request.OutputAsset is null || request.Budget is null || credential.ActorUserId == Guid.Empty || credential.TenantId == Guid.Empty ||
            request.SlippageBps is < 0 or >= 10000 || request.GrossInput is not { Length: > 0 and <= 512 } ||
            !UnsignedIntegerText.TryParse(request.GrossInput, out var gross) || gross <= 0 ||
            !ValidBudget(request.Budget) || !ValidAsset(request.InputAsset) || !ValidAsset(request.OutputAsset) ||
            request.InputAsset.Id == request.OutputAsset.Id)
            return AZOAResult<DexQuoteAvailability>.FailureWithCode("Invalid exact-input quote request.", AzoaErrorCodes.InvalidRequest);
        var module = modules.Resolve(request.Network);
        if (module is null) return Unavailable("No reviewed DEX module is available for this exact network.");
        var wallet = await wallets.GetByIdAsync(request.WalletId, ct);
        if (wallet.IsError || wallet.Result is null || wallet.Result.UserId != credential.ActorUserId ||
            wallet.Result.ChainType != request.Network.ChainType)
            return AZOAResult<DexQuoteAvailability>.FailureWithCode("The quote wallet is not owned by the authenticated participant.", "Forbidden");
        var signing = await authority.BindDirectAsync(credential, request.WalletId, wallet.Result.ChainType,
            wallet.Result.Address, [AzoaScopes.SwapSign], ct);
        if (signing.IsError || signing.Result is null)
            return AZOAResult<DexQuoteAvailability>.FailureWithCode(signing.Message, "Forbidden");
        var feeResult = await fees.QuoteSwapBaseUnitsAsync(request.GrossInput, ct);
        if (feeResult.IsError || feeResult.Result is not { } fee) return Unavailable(feeResult.Message);
        var policy = configuration.GetSection($"Dex:FeePolicies:{request.Network.ChainType}:{request.Network.Network}");
        var treasury = policy["TreasuryAddress"] ?? "";
        if (fee.FeeAmount != "0" && string.IsNullOrWhiteSpace(treasury)) return Unavailable("The input-asset fee treasury is not configured.");
        var owner = new DexParticipant(credential.ActorUserId, request.WalletId, wallet.Result.Address);
        var proposed = new DexQuoteTerms(1, credential.ActorUserId, credential.TenantId, owner, owner, owner.Address,
            request.Network, request.InputAsset, request.OutputAsset, request.GrossInput, fee.NetAmount, "0", "0", request.SlippageBps,
            new(fee.Denomination!, fee.ScheduleVersion, fee.FlatBaseUnits, checked((int)fee.Bps), fee.FeeAmount, treasury,
                policy["PolicyKeyVersion"] ?? ""), request.Budget, module.DexCapability.Provider, module.DexCapability.ProtocolVersion,
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        var quoted = await module.QuoteSwapAsync(proposed, ct);
        if (quoted.IsError && quoted.Code == AzoaErrorCodes.DependencyUnavailable) return Unavailable(quoted.Message);
        if (quoted.IsError) return AZOAResult<DexQuoteAvailability>.FailureWithCode(quoted.Message, quoted.Code ?? AzoaErrorCodes.InvalidRequest);
        if (quoted.Result is not { } terms) return Unavailable(quoted.Message);
        if (!SameRequest(proposed, terms) || terms.ExpiresAt <= DateTimeOffset.UtcNow || terms.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
            terms.Prerequisites is null || !UnsignedIntegerText.TryParse(terms.ExpectedOutput, out var expected) ||
            !UnsignedIntegerText.TryParse(terms.MinimumOutput, out var minimum) || minimum <= 0 || minimum > expected ||
            terms.ProtocolEvidence.ValueKind != JsonValueKind.Object)
            return AZOAResult<DexQuoteAvailability>.Failure("Provider quote changed its immutable participant, denomination or fee policy.");
        var id = Guid.NewGuid().ToString("N");
        var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms));
        var hash = CanonicalJson.HashCanonical(json);
        var saved = await quotes.ClaimAsync(new DexQuoteRecord
        {
            Id = id, ActorUserId = credential.ActorUserId.ToString("N"), TenantId = credential.TenantId?.ToString("N") ?? "",
            QuoteHash = hash, QuoteJson = json, ExpiresAt = terms.ExpiresAt, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        return saved is null ? AZOAResult<DexQuoteAvailability>.FailureWithCode("Quote persistence conflicted; request a new quote.", "Conflict")
            : AZOAResult<DexQuoteAvailability>.Success(new(false, new(id, hash, terms)));
    }

    public async Task<DexQuoteView?> GetAsync(string quoteId, SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(quoteId, "N", out var id) || id == Guid.Empty) return null;
        var row = await quotes.GetAsync(quoteId, ct);
        return row is null || row.ActorUserId != credential.ActorUserId.ToString("N") ||
            row.TenantId != (credential.TenantId?.ToString("N") ?? "") ? null
            : new(quoteId, row.QuoteHash, JsonSerializer.Deserialize<DexQuoteTerms>(row.QuoteJson)!);
    }

    private static bool SameRequest(DexQuoteTerms expected, DexQuoteTerms actual)
        => actual.Version == expected.Version && actual.ActorUserId == expected.ActorUserId && actual.TenantId == expected.TenantId &&
            actual.Owner == expected.Owner && actual.Payer == expected.Payer && actual.RecipientAddress == expected.RecipientAddress &&
            actual.Network == expected.Network && actual.GrossInput == expected.GrossInput && actual.SwapInput == expected.SwapInput &&
            actual.SlippageBps == expected.SlippageBps && actual.PlatformFee == expected.PlatformFee && actual.Budget == expected.Budget &&
            actual.Provider == expected.Provider && actual.ProtocolVersion == expected.ProtocolVersion &&
            actual.InputAsset is { } input && actual.OutputAsset is { } output && ValidAsset(input) && ValidAsset(output) && input.Id == expected.InputAsset.Id &&
            input.Decimals == expected.InputAsset.Decimals && input.Native == expected.InputAsset.Native &&
            output.Id == expected.OutputAsset.Id && output.Decimals == expected.OutputAsset.Decimals && output.Native == expected.OutputAsset.Native;

    private static bool ValidAsset(DexAsset asset) => asset.Id is { Length: > 0 and <= 256 } &&
        asset.Symbol is { Length: <= 64 } && asset.Decimals is >= 0 and <= 255;
    private static bool ValidBudget(DexBudget budget) => ValidAmount(budget.MaximumNetworkFee) &&
        ValidAmount(budget.MaximumLockedBalance) && ValidAmount(budget.MaximumCleanupFee);
    private static bool ValidAmount(string value) => value is { Length: > 0 and <= 512 } && UnsignedIntegerText.TryParse(value, out _);
    private static AZOAResult<DexQuoteAvailability> Unavailable(string message)
        => AZOAResult<DexQuoteAvailability>.Success(new(true, Reason: message));
}
