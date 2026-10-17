using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="AZOA.WebAPI.Interfaces.IDexTransactionModule.QuoteSwapAsync"/>
    public async Task<AZOAResult<DexQuoteTerms>> QuoteSwapAsync(DexQuoteTerms requested, CancellationToken ct = default)
    {
        try { return await QuoteTinymanAsync(requested, ct); }
        catch (HttpRequestException) { return TinymanUnavailable("Tinyman network data is temporarily unavailable."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return TinymanUnavailable("Tinyman network data timed out."); }
        catch (JsonException) { return TinymanUnavailable("Tinyman network data has an invalid response shape."); }
    }

    private async Task<AZOAResult<DexQuoteTerms>> QuoteTinymanAsync(DexQuoteTerms requested, CancellationToken ct)
    {
        var client = AlgodHttpClient;
        if (requested.Version != 1 || requested.Network is null || requested.Owner is null || requested.Payer is null ||
            requested.InputAsset is null || requested.OutputAsset is null || requested.PlatformFee is null || requested.Budget is null ||
            requested.Network.ChainType != ChainType ||
            requested.Network.Network != ActiveNetwork.ToString() || requested.Owner != requested.Payer ||
            requested.Owner.UserId != requested.ActorUserId || requested.RecipientAddress != requested.Owner.Address ||
            !ValidateAddressFormat(requested.Owner.Address) ||
            !UInt64Text.TryParse(requested.InputAsset.Id, out var inputAsset) ||
            !UInt64Text.TryParse(requested.OutputAsset.Id, out var outputAsset) || inputAsset == outputAsset ||
            requested.InputAsset.Native != (inputAsset == 0) || requested.OutputAsset.Native != (outputAsset == 0) ||
            !UInt64Text.TryParse(requested.GrossInput, out var gross) ||
            !UInt64Text.TryParse(requested.SwapInput, out var input) || input == 0 ||
            !UInt64Text.TryParse(requested.PlatformFee.Amount, out var platformFee) || gross < platformFee || gross - platformFee != input ||
            requested.PlatformFee.Denomination != NodeFeeSchedule.InputBaseUnitsV1 ||
            (platformFee > 0 && !ValidateAddressFormat(requested.PlatformFee.TreasuryAddress)) ||
            requested.SlippageBps is < 0 or >= 10000)
            return AZOAResult<DexQuoteTerms>.FailureWithCode("Invalid Tinyman quote identity or exact-input amounts.", AzoaErrorCodes.InvalidRequest);
        var configuration = _config.GetSection($"Dex:Tinyman:Networks:{ActiveNetwork}");
        if (IndexerHttpClient is null || !UInt64Text.TryParse(configuration["ValidatorAppId"], out var validator) || validator == 0 ||
            !DigestText(configuration["ApprovalSha256"]) || !DigestText(configuration["ClearStateSha256"]) ||
            string.IsNullOrWhiteSpace(configuration["GenesisHash"]))
            return TinymanUnavailable("Tinyman validator code/genesis pins and indexer must be configured for this network.");
        var before = await client.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod transaction parameters are missing.");
        if (before.GenesisHashB64 != configuration["GenesisHash"] ||
            requested.Network.ProtocolIdentity != "algorand:" + before.GenesisHashB64)
            return TinymanUnavailable("Tinyman chain genesis does not match the admitted network.");
        var application = await ReadTinymanJsonAsync($"/v2/applications/{validator}", ct);
        if (application is null) return TinymanUnavailable("The configured Tinyman validator is unavailable.");
        if (application.Value.ValueKind != JsonValueKind.Object ||
            !application.Value.TryGetProperty("id", out var applicationId) || applicationId.ValueKind != JsonValueKind.Number ||
            !applicationId.TryGetUInt64(out var actualApplication) || actualApplication != validator ||
            !application.Value.TryGetProperty("params", out var parameters) ||
            !ProgramMatches(parameters, "approval-program", configuration["ApprovalSha256"]!) ||
            !ProgramMatches(parameters, "clear-state-program", configuration["ClearStateSha256"]!))
            return TinymanUnavailable("The Tinyman validator programs do not match their reviewed deployment pins.");
        var poolAddress = TinymanV2PoolLocator.GetPoolAddress(validator, inputAsset, outputAsset);
        var account = await ReadTinymanJsonAsync("/v2/accounts/" + poolAddress, ct);
        if (account is null || !TinymanPoolState.TryRead(account.Value, validator, inputAsset, outputAsset, out var pool))
            return TinymanUnavailable("No validated Tinyman pool is available for this pair.");
        var after = await client.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod transaction parameters are missing.");
        if (after.GenesisHashB64 != before.GenesisHashB64 || after.LastRound < 0 ||
            pool!.Round > (ulong)after.LastRound || (ulong)after.LastRound - pool.Round > 10)
            return TinymanUnavailable("Tinyman pool snapshot is stale or from a different chain.");
        if (!pool.TryQuote(inputAsset, input, out var quote))
            return TinymanUnavailable("Tinyman liquidity or current fee constraints cannot satisfy this input.");
        var inputMetadata = await ReadTinymanAssetAsync(inputAsset, ct);
        var outputMetadata = await ReadTinymanAssetAsync(outputAsset, ct);
        if (inputMetadata is null || outputMetadata is null ||
            inputMetadata.Asset.Decimals != requested.InputAsset.Decimals || outputMetadata.Asset.Decimals != requested.OutputAsset.Decimals)
            return TinymanUnavailable("Asset metadata does not match the requested base-unit denomination.");
        var minimum = (ulong)((UInt128)quote!.Output * (ulong)(10000 - requested.SlippageBps) / 10000);
        if (minimum == 0) return TinymanUnavailable("The quoted minimum output rounds to zero.");
        var owner = await ReadTinymanJsonAsync("/v2/accounts/" + requested.Owner.Address, ct);
        if (owner is not null && owner.Value.TryGetProperty("auth-addr", out var authAddress) &&
            (authAddress.ValueKind != JsonValueKind.String || authAddress.GetString() != requested.Owner.Address))
            return TinymanUnavailable("The owner account requires a delegated signature outside this route's signing grammar.");
        if (owner is null || !TinymanHolding(owner.Value, requested.Owner.Address, inputAsset, out var inputBalance, out var inputPresent) ||
            !inputPresent || inputBalance < gross ||
            !TinymanHolding(owner.Value, requested.Owner.Address, outputAsset, out _, out var outputPresent) ||
            !TinymanHolding(owner.Value, requested.Owner.Address, 0, out var spendableAlgo, out _))
            return TinymanUnavailable("The owner lacks an available input holding or valid account balance.");
        var rent = outputPresent ? 0UL : 100000UL;
        if (!outputPresent && outputMetadata.DefaultFrozen != false)
            return TinymanUnavailable("An output opt-in requires authoritative unfrozen asset defaults.");
        if (!UInt64Text.TryParse(requested.Budget.MaximumNetworkFee, out var networkCap) ||
            !UInt64Text.TryParse(requested.Budget.MaximumLockedBalance, out var rentCap) || rent > rentCap || after.MinFee <= 0 ||
            (UInt128)(ulong)after.MinFee * (ulong)(3 + (platformFee > 0 ? 1 : 0) + (outputPresent ? 0 : 1)) > networkCap ||
            (UInt128)(inputAsset == 0 ? gross : 0UL) + networkCap + rent > spendableAlgo)
            return TinymanUnavailable("The authorized fee/rent caps or available ALGO cannot cover this swap and required opt-in.");
        if (platformFee > 0)
        {
            var treasury = await ReadTinymanJsonAsync("/v2/accounts/" + requested.PlatformFee.TreasuryAddress, ct);
            if (treasury is null || !TinymanHolding(treasury.Value, requested.PlatformFee.TreasuryAddress,
                inputAsset, out _, out var treasuryPresent) || !treasuryPresent)
                return TinymanUnavailable("The configured treasury cannot receive the input asset fee.");
        }
        IReadOnlyList<DexEffectDisclosure> prerequisites = outputPresent ? [] :
            [new("OptIn", outputAsset.ToString(CultureInfo.InvariantCulture), rent.ToString(CultureInfo.InvariantCulture),
                requested.Owner.Address, requested.Owner.Address, true)];
        var evidence = JsonSerializer.SerializeToElement(new
        {
            format = "tinyman-v2-pool/1", validator = validator.ToString(CultureInfo.InvariantCulture), pool = poolAddress,
            approvalSha256 = configuration["ApprovalSha256"], clearStateSha256 = configuration["ClearStateSha256"],
            round = pool.Round.ToString(CultureInfo.InvariantCulture),
            asset1 = pool.Asset1.ToString(CultureInfo.InvariantCulture), asset2 = pool.Asset2.ToString(CultureInfo.InvariantCulture),
            reserve1 = pool.Reserve1.ToString(CultureInfo.InvariantCulture), reserve2 = pool.Reserve2.ToString(CultureInfo.InvariantCulture),
            protocolFees1 = pool.ProtocolFees1.ToString(CultureInfo.InvariantCulture), protocolFees2 = pool.ProtocolFees2.ToString(CultureInfo.InvariantCulture),
            totalFeeShare = pool.TotalFeeShare.ToString(CultureInfo.InvariantCulture), protocolFeeRatio = pool.ProtocolFeeRatio.ToString(CultureInfo.InvariantCulture),
            liquidityProviderFee = quote.PoolFee.ToString(CultureInfo.InvariantCulture), protocolFee = quote.ProtocolFee.ToString(CultureInfo.InvariantCulture),
        });
        return AZOAResult<DexQuoteTerms>.Success(requested with
        {
            InputAsset = inputMetadata.Asset, OutputAsset = outputMetadata.Asset, Provider = "Tinyman", ProtocolVersion = "2",
            ExpectedOutput = quote.Output.ToString(CultureInfo.InvariantCulture), MinimumOutput = minimum.ToString(CultureInfo.InvariantCulture),
            ProtocolEvidence = evidence, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30), Prerequisites = prerequisites,
        });
    }

    private async Task<JsonElement?> ReadTinymanJsonAsync(string path, CancellationToken ct)
    {
        using var response = await AlgodHttpClient.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        _ = CanonicalJson.Canonicalize(document.RootElement);
        return document.RootElement.Clone();
    }

    private sealed record TinymanAssetMetadata(DexAsset Asset, bool? DefaultFrozen);

    private async Task<TinymanAssetMetadata?> ReadTinymanAssetAsync(ulong asset, CancellationToken ct)
    {
        if (asset == 0) return new(new("0", "ALGO", 6, true), false);
        var response = await ReadTinymanJsonAsync($"/v2/assets/{asset}", ct);
        if (response is null || response.Value.ValueKind != JsonValueKind.Object || !response.Value.TryGetProperty("index", out var index) ||
            index.ValueKind != JsonValueKind.Number || !index.TryGetUInt64(out var actualId) || actualId != asset ||
            !response.Value.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("decimals", out var decimals) || decimals.ValueKind != JsonValueKind.Number ||
            !decimals.TryGetInt32(out var precision) || precision is < 0 or > 19) return null;
        var symbol = parameters.TryGetProperty("unit-name", out var unit) && unit.ValueKind == JsonValueKind.String
            ? unit.GetString()! : asset.ToString(CultureInfo.InvariantCulture);
        bool? defaultFrozen = parameters.TryGetProperty("default-frozen", out var frozen) &&
            frozen.ValueKind is JsonValueKind.True or JsonValueKind.False ? frozen.GetBoolean() : null;
        return new(new(asset.ToString(CultureInfo.InvariantCulture), symbol, precision, false), defaultFrozen);
    }

    private static bool DigestText(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ProgramMatches(JsonElement parameters, string name, string expected)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String) return false;
        try
        {
            var encoded = value.GetString()!;
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length > 0 && Convert.ToBase64String(bytes) == encoded &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected;
        }
        catch (FormatException) { return false; }
    }

    private static AZOAResult<DexQuoteTerms> TinymanUnavailable(string message)
        => AZOAResult<DexQuoteTerms>.FailureWithCode(message, AzoaErrorCodes.DependencyUnavailable);

    private static bool TinymanHolding(JsonElement account, string expectedAddress, ulong asset, out ulong balance, out bool present)
    {
        balance = 0;
        present = false;
        if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("address", out var address) ||
            address.ValueKind != JsonValueKind.String || address.GetString() != expectedAddress) return false;
        if (asset == 0)
        {
            if (!account.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number ||
                !amount.TryGetUInt64(out var total) || !account.TryGetProperty("min-balance", out var minimum) ||
                minimum.ValueKind != JsonValueKind.Number || !minimum.TryGetUInt64(out var locked) || total < locked) return false;
            balance = total - locked;
            present = true;
            return true;
        }
        if (!account.TryGetProperty("assets", out var assets)) return true;
        if (assets.ValueKind != JsonValueKind.Array) return false;
        var identities = new HashSet<ulong>();
        foreach (var holding in assets.EnumerateArray())
        {
            if (holding.ValueKind != JsonValueKind.Object || !holding.TryGetProperty("asset-id", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out var value) || !identities.Add(value)) return false;
            if (value != asset) continue;
            if (!holding.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number ||
                !amount.TryGetUInt64(out balance) || !holding.TryGetProperty("is-frozen", out var frozen) ||
                frozen.ValueKind != JsonValueKind.False) return false;
            present = true;
        }
        return true;
    }
}
