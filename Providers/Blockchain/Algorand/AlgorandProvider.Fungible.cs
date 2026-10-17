using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AlgorandEncoder = Algorand.Utils.Encoder;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IFungibleCreationModule
{
    private sealed record FungiblePlan(string UnsignedBase64, string ProtocolIdentity);

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationQuote>> QuoteFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default)
    {
        if (!ValidFungibleIntent(intent) || IndexerHttpClient is null
            || (intent.SigningContext.IsPlatform && intent.CreatorAddress != ResolvePlatformAddress()))
            return AZOAResult<FungibleCreationQuote>.Failure("Valid creation intent and an authoritative indexer are required.");
        var parameters = await ValidatedTransactionParametersAsync(ct);
        var tx = BuildFungibleTransaction(intent, parameters);
        tx.Fee = AvmFee(tx, parameters);
        tx.Fee = AvmFee(tx, parameters);
        var quote = new FungibleCreationQuote("", FungibleCreationIdentity.IntentHash(intent),
            "algorand:" + parameters.GenesisHashB64, tx.Fee!.Value.ToString(CultureInfo.InvariantCulture),
            "100000", NativeCurrencyCode, NativeCurrencyDecimals, intent.CreatorAddress, DateTimeOffset.UtcNow.AddMinutes(10));
        return AZOAResult<FungibleCreationQuote>.Success(quote with { QuoteId = FungibleCreationIdentity.QuoteId(quote) });
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedFungibleCreation>> PrepareFungibleCreationAsync(FungibleCreationIntent intent, CancellationToken ct = default)
    {
        if (!ValidFungibleIntent(intent) || IndexerHttpClient is null || !ValidFungibleQuote(intent))
            return AZOAResult<PreparedFungibleCreation>.Failure("Creation quote or immutable intent is invalid.");
        var identity = FungibleCreationIdentity.PreparationId(intent);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var row = await store.GetAsync(identity, ct);
            if (row is null)
            {
                var parameters = await ValidatedTransactionParametersAsync(ct);
                if (intent.Quote!.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
                    return AZOAResult<PreparedFungibleCreation>.Failure("Creation quote belongs to another network.");
                var tx = BuildFungibleTransaction(intent, parameters);
                tx.Fee = UInt64Text.Parse(intent.Quote.NetworkFeeBaseUnits);
                if (tx.Fee < AvmFee(tx, parameters))
                    return AZOAResult<PreparedFungibleCreation>.Failure("Current network cost exceeds the accepted creation quote.");
                row = await store.ClaimAsync(new NativePreparation
                {
                    Id = identity, IntentHash = hash, Stage = "FungibleCreate", Generation = intent.Generation,
                    PlanJson = JsonSerializer.Serialize(new FungiblePlan(Convert.ToBase64String(AlgorandEncoder.EncodeToMsgPackOrdered(tx)), intent.Quote.ProtocolIdentity)),
                    CreatedAt = DateTimeOffset.UtcNow,
                }, ct);
            }
            if (row.IntentHash != hash || row.Stage != "FungibleCreate" || row.Generation != intent.Generation)
                return AZOAResult<PreparedFungibleCreation>.Failure("Creation preparation conflicts with the saved intent.");
            if (row.PreparedJson is not null)
                return AZOAResult<PreparedFungibleCreation>.Success(JsonSerializer.Deserialize<PreparedFungibleCreation>(row.PreparedJson)!);
            if (intent.SigningContext.IsPlatform && intent.CreatorAddress != ResolvePlatformAddress())
                return AZOAResult<PreparedFungibleCreation>.Failure("The original creator signing key is not currently available.");
            var plan = JsonSerializer.Deserialize<FungiblePlan>(row.PlanJson) ?? throw new InvalidOperationException("Creation plan is missing.");
            var unsigned = Convert.FromBase64String(plan.UnsignedBase64);
            var signed = await SignViaCustodyAsync(unsigned, "prepare fungible creation", intent.SigningContext);
            if (signed.IsError || signed.Result is null)
                return AZOAResult<PreparedFungibleCreation>.Failure(signed.Message);
            var envelope = AlgorandSignedEnvelope.DecodeCanonical(Convert.ToBase64String(signed.Result));
            if (!AlgorandEncoder.EncodeToMsgPackOrdered(envelope.Tx).AsSpan().SequenceEqual(unsigned))
                throw new InvalidOperationException("Custody changed the saved creation plan.");
            AlgorandSignedEnvelope.RequireSingleSender(envelope, intent.CreatorAddress);
            var prepared = new PreparedFungibleCreation(identity, envelope.Tx.TxID(), Convert.ToBase64String(signed.Result), plan.ProtocolIdentity, intent);
            var saved = await store.SavePreparedAsync(identity, hash, JsonSerializer.Serialize(prepared), ct);
            return AZOAResult<PreparedFungibleCreation>.Success(JsonSerializer.Deserialize<PreparedFungibleCreation>(saved.PreparedJson!)!);
        });
    }

    /// <inheritdoc/>
    public async Task SubmitFungibleCreationAsync(PreparedFungibleCreation prepared, CancellationToken ct = default)
    {
        var tx = await ValidateFungiblePreparedAsync(prepared, ct);
        var parameters = await ValidatedTransactionParametersAsync(ct);
        if (prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            throw new InvalidOperationException("Creation belongs to another chain genesis.");
        if ((ulong)parameters.LastRound > tx.LastValid) return;
        using var content = new ByteArrayContent(Convert.FromBase64String(prepared.SignedTransactionBase64));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!result.RootElement.TryGetProperty("txId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != prepared.TransactionId)
            throw new HttpRequestException("Algod returned a different creation transaction identity.");
    }

    private async Task<AssetCreateTransaction> ValidateFungiblePreparedAsync(PreparedFungibleCreation prepared, CancellationToken ct)
    {
        if (!ValidFungibleIntent(prepared.Intent) || !ValidFungibleQuote(prepared.Intent)
            || prepared.PreparationId != FungibleCreationIdentity.PreparationId(prepared.Intent))
            throw new InvalidOperationException("Prepared creation identity is invalid.");
        var row = await NativePreparationJournal.UseAsync(_custodyScopeFactory, store => store.GetAsync(prepared.PreparationId, ct));
        if (row is null || row.IntentHash != CanonicalJson.Hash(prepared.Intent) || row.Stage != "FungibleCreate"
            || row.Generation != prepared.Intent.Generation || row.PreparedJson != JsonSerializer.Serialize(prepared))
            throw new InvalidOperationException("Creation wire is not its saved signed candidate.");
        var plan = JsonSerializer.Deserialize<FungiblePlan>(row.PlanJson) ?? throw new InvalidOperationException("Creation plan is missing.");
        var signed = AlgorandSignedEnvelope.DecodeCanonical(prepared.SignedTransactionBase64);
        if (signed.Tx is not AssetCreateTransaction tx || tx.TxID() != prepared.TransactionId
            || plan.ProtocolIdentity != prepared.ProtocolIdentity || prepared.Intent.Quote!.ProtocolIdentity != prepared.ProtocolIdentity
            || !AlgorandEncoder.EncodeToMsgPackOrdered(tx).AsSpan().SequenceEqual(Convert.FromBase64String(plan.UnsignedBase64)))
            throw new InvalidOperationException("Creation wire differs from its immutable plan.");
        var intent = prepared.Intent;
        var expected = BuildAssetParams(intent.Name, intent.UnitName, UInt64Text.Parse(intent.TotalBaseUnits), (ulong)intent.Decimals,
            intent.DefaultFrozen, intent.ManagerAddress, intent.ReserveAddress, intent.FreezeAddress, intent.ClawbackAddress,
            intent.Url, intent.MetadataHashBase64 is null ? null : Convert.FromBase64String(intent.MetadataHashBase64));
        if (!AlgorandEncoder.EncodeToMsgPackOrdered(tx.AssetParams).AsSpan().SequenceEqual(AlgorandEncoder.EncodeToMsgPackOrdered(expected))
            || tx.GenesisHash is null || prepared.ProtocolIdentity != "algorand:" + Convert.ToBase64String(tx.GenesisHash.Bytes)
            || tx.Fee != UInt64Text.Parse(intent.Quote!.NetworkFeeBaseUnits) || tx.FirstValid is null || tx.LastValid is null
            || tx.FirstValid > tx.LastValid || tx.Group is not null || tx.RekeyTo is not null
            || tx.Note is null || !tx.Note.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(FungibleCreationIdentity.IntentHash(intent))))
            throw new InvalidOperationException("Creation wire does not implement the authorized intent.");
        AlgorandSignedEnvelope.RequireSingleSender(signed, prepared.Intent.CreatorAddress);
        return tx;
    }

    private bool ValidFungibleIntent(FungibleCreationIntent intent)
    {
        if (intent.ChainType != ChainType || intent.Network != ActiveNetwork || intent.ActorUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(intent.PreparationKey) || intent.Generation < 0
            || !ValidateAddressFormat(intent.CreatorAddress) || !UInt64Text.TryParse(intent.TotalBaseUnits, out var supply) || supply == 0
            || intent.Decimals is < 0 or > 19 || string.IsNullOrWhiteSpace(intent.Name) || Encoding.UTF8.GetByteCount(intent.Name) > 32
            || string.IsNullOrWhiteSpace(intent.UnitName) || Encoding.UTF8.GetByteCount(intent.UnitName) > 8
            || Encoding.UTF8.GetByteCount(intent.Url ?? "") > 96
            || new[] { intent.ManagerAddress, intent.ReserveAddress, intent.FreezeAddress, intent.ClawbackAddress }
                .Any(address => address is null || (address.Length != 0 && !ValidateAddressFormat(address)))) return false;
        if (intent.SigningContext.Scope != AzoaScopes.TokenCreateSign
            || (!intent.SigningContext.IsPlatform && (!intent.SigningContext.IsResolvableUserContext || intent.SigningContext.UserId != intent.ActorUserId))) return false;
        if (intent.SigningContext.ActingTenantId != (intent.TenantId ?? Guid.Empty)
            || (intent.TenantId.HasValue && (intent.TenantId == Guid.Empty || intent.SigningContext.GrantorUserId != intent.ActorUserId
                || intent.SigningContext.Scope != AzoaScopes.TokenCreateSign))) return false;
        if (intent.MetadataHashBase64 is null) return true;
        var metadata = new byte[32];
        return Convert.TryFromBase64String(intent.MetadataHashBase64, metadata, out var length) && length == 32
            && Convert.ToBase64String(metadata) == intent.MetadataHashBase64;
    }

    private bool ValidFungibleQuote(FungibleCreationIntent intent)
        => FungibleCreationIdentity.IsValidQuote(intent) && intent.Quote is { } quote
            && quote.Currency == NativeCurrencyCode && quote.Decimals == NativeCurrencyDecimals
            && quote.MinimumBalanceBaseUnits == "100000" && UInt64Text.TryParse(quote.NetworkFeeBaseUnits, out var fee) && fee > 0;

    private AssetCreateTransaction BuildFungibleTransaction(FungibleCreationIntent intent, AlgodSuggestedParams parameters)
    {
        var tx = new AssetCreateTransaction
        {
            Sender = new Address(intent.CreatorAddress),
            AssetParams = BuildAssetParams(intent.Name, intent.UnitName, UInt64Text.Parse(intent.TotalBaseUnits), (ulong)intent.Decimals,
                intent.DefaultFrozen, intent.ManagerAddress, intent.ReserveAddress, intent.FreezeAddress, intent.ClawbackAddress,
                intent.Url, intent.MetadataHashBase64 is null ? null : Convert.FromBase64String(intent.MetadataHashBase64)),
            Note = Encoding.UTF8.GetBytes(FungibleCreationIdentity.IntentHash(intent)),
        };
        ApplySuggestedParams(tx, parameters);
        return tx;
    }

    private async Task<AlgodSuggestedParams> ValidatedTransactionParametersAsync(CancellationToken ct)
    {
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new InvalidOperationException("Algod transaction parameters are unavailable.");
        var genesis = new byte[32];
        if (parameters.LastRound < 0 || parameters.Fee < 0 || parameters.MinFee < 0
            || !Convert.TryFromBase64String(parameters.GenesisHashB64 ?? "", genesis, out var length) || length != 32
            || Convert.ToBase64String(genesis) != parameters.GenesisHashB64)
            throw new InvalidDataException("Algod supplied malformed genesis, round or fee evidence.");
        return parameters;
    }

}
