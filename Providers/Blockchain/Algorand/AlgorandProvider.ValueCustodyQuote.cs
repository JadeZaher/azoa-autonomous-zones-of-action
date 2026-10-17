using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private sealed record ValueAccount(ulong NativeAmount, ulong MinimumBalance, ulong AssetAmount, bool OptedIn, bool Frozen);

    /// <inheritdoc/>
    public async Task<AZOAResult<ValueCustodyQuote>> QuoteValueCustodyAsync(ValueCustodyQuoteRequest request, CancellationToken ct)
    {
        if (request.ChainType != ChainType || request.Network != ActiveNetwork || IndexerHttpClient is null
            || !UInt64Text.TryParse(request.AssetId, out var asset) || asset == 0
            || !UInt64Text.TryParse(request.QuantityBaseUnits, out var amount) || amount == 0
            || !ValidateAddressFormat(request.Sender) || !ValidateAddressFormat(request.Recipient)
            || !ValidateAddressFormat(request.HoldingAddress))
            return AZOAResult<ValueCustodyQuote>.Failure("An exact asset, physical custody account and participants are required.");
        var parameters = await ValidatedTransactionParametersAsync(ct);
        using var response = await AlgodHttpClient.GetAsync($"/v2/assets/{asset}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return AZOAResult<ValueCustodyQuote>.Failure("The custody asset does not exist on this network.");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !AlgorandReceiptFields.Number(root, "index", asset)
            || !root.TryGetProperty("params", out var fields) || fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty("creator", out var creator) || creator.ValueKind != JsonValueKind.String
            || !ValidateAddressFormat(creator.GetString()!)
            || !ValueUnsigned(fields, "total", out var supply) || supply < amount
            || !ValueUnsigned(fields, "decimals", out var decimals) || decimals > 19
            || (fields.TryGetProperty("default-frozen", out var defaultFrozen) && defaultFrozen.ValueKind != JsonValueKind.False)
            || !ValueAuthority(fields, "manager", request.HoldingAddress)
            || !ValueAuthority(fields, "freeze", request.HoldingAddress)
            || !ValueAuthority(fields, "clawback", request.HoldingAddress))
            return AZOAResult<ValueCustodyQuote>.Failure("Asset mechanics or custody authority cannot guarantee the promised net amount.");
        var sender = await ReadValueAccountAsync(request.Sender, asset, ct);
        var recipient = request.Sender == request.Recipient ? sender : await ReadValueAccountAsync(request.Recipient, asset, ct);
        var holding = request.HoldingAddress == request.Sender ? sender : request.HoldingAddress == request.Recipient ? recipient
            : await ReadValueAccountAsync(request.HoldingAddress, asset, ct);
        if (sender is null || recipient is null || holding is null || !sender.OptedIn || sender.Frozen
            || sender.AssetAmount < amount || recipient.Frozen || !holding.OptedIn || holding.Frozen)
            return AZOAResult<ValueCustodyQuote>.Failure("Current source balance, unfrozen holding and account authority must be established.");
        var transfer = new AssetTransferTransaction { Sender = new Address(request.Sender),
            AssetReceiver = new Address(request.Recipient), XferAsset = asset, AssetAmount = amount,
            Note = Encoding.UTF8.GetBytes(new string('0', 64)) };
        ApplySuggestedParams(transfer, parameters);
        transfer.Fee = AvmFee(transfer, parameters);
        transfer.Fee = AvmFee(transfer, parameters);
        var accept = new AssetAcceptTransaction { Sender = new Address(request.Recipient), AssetReceiver = new Address(request.Recipient), XferAsset = asset,
            Note = Encoding.UTF8.GetBytes(new string('0', 64)) };
        ApplySuggestedParams(accept, parameters);
        accept.Fee = AvmFee(accept, parameters);
        accept.Fee = AvmFee(accept, parameters);
        if (sender.NativeAmount < sender.MinimumBalance || sender.NativeAmount - sender.MinimumBalance < transfer.Fee!.Value)
            return AZOAResult<ValueCustodyQuote>.Failure("The sender needs native currency above its account minimum for the quoted network fee.");
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, string.Empty, request, "algorand:" + parameters.GenesisHashB64,
            CanonicalJson.Hash(fields), NativeCurrencyCode, NativeCurrencyDecimals,
            transfer.Fee.Value.ToString(CultureInfo.InvariantCulture), recipient.OptedIn,
            recipient.OptedIn ? "0" : accept.Fee!.Value.ToString(CultureInfo.InvariantCulture),
            recipient.OptedIn ? "0" : "100000", now, now.AddMinutes(10),
            new(request.Sender, request.Sender, transfer.Fee.Value.ToString(CultureInfo.InvariantCulture), "0", "0"));
        return AZOAResult<ValueCustodyQuote>.Success(quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) });
    }

    private async Task<ValueAccount?> ReadValueAccountAsync(string address, ulong asset, CancellationToken ct)
    {
        using var response = await AlgodHttpClient.GetAsync($"/v2/accounts/{address}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(0, 100000, 0, false, false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !AlgorandReceiptFields.String(root, "address", address)
            || !ValueUnsigned(root, "amount", out var native) || !ValueUnsigned(root, "min-balance", out var minimum)
            || (root.TryGetProperty("auth-addr", out var auth)
                && (auth.ValueKind != JsonValueKind.String || auth.GetString() != address))) return null;
        var amount = 0UL;
        var found = false;
        var frozen = false;
        if (root.TryGetProperty("assets", out var assets))
        {
            if (assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in assets.EnumerateArray())
            {
                if (!ValueUnsigned(item, "asset-id", out var itemId)) return null;
                if (itemId != asset) continue;
                if (found || !ValueUnsigned(item, "amount", out amount)
                    || !item.TryGetProperty("is-frozen", out var flag)
                    || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                found = true;
                frozen = flag.GetBoolean();
            }
        }
        return new(native, minimum, amount, found, frozen);
    }

    private static bool ValueUnsigned(JsonElement fields, string name, out ulong number)
    {
        number = 0;
        return fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out number);
    }

    private static bool ValueAuthority(JsonElement fields, string name, string holdingAddress)
        => !fields.TryGetProperty(name, out var value) || (value.ValueKind == JsonValueKind.String
            && (string.IsNullOrEmpty(value.GetString()) || value.GetString() == holdingAddress
                || value.GetString() == new Address(new byte[32]).EncodeAsString()));
}
