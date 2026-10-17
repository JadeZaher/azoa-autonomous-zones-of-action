using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Algorand;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IWttSourceModule
{
    /// <inheritdoc/>
    public Task<AZOAResult<WttPublicationRules>> ReadWttPublicationRulesAsync(CancellationToken ct = default)
    {
        try
        {
            _ = WttAlgorandDeployment();
            return Task.FromResult(Ok(new WttPublicationRules(0, true)));
        }
        catch (InvalidDataException exception) { return Task.FromResult(Error<WttPublicationRules>(exception.Message)); }
    }

    /// <inheritdoc cref="IWttSourceModule.QuoteWttSourceAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttSourceAsync(WttOperationIntent intent, CancellationToken ct = default)
    {
        if (!ValidWttAlgoSourceIntent(intent)) return AZOAResult<WttStageQuote>.Failure("Exact Algorand source intent and custody context are required.");
        try
        {
            var snapshot = await ReadWttAlgoSnapshotAsync(null, 0, null, ct);
            var asset = await ReadWttAlgoAssetAsync(snapshot, intent.Asset.TokenAddress, intent.Asset.NativeAsset, ct);
            if (WttAlgoDescriptor(snapshot) != intent.Source || asset != intent.Asset)
                throw new InvalidDataException("Actual source deployment or ASA origin differs from the immutable intent.");
            var amount = UInt64Text.Parse(intent.Amount.Debited);
            var fee = WttPayloadCodec.ParseAmount(intent.Amount.Fee) * BigInteger.Pow(10, Math.Max(0, asset.Decimals - 8));
            if (amount == 0 || fee > ulong.MaxValue || intent.Nonce != 0 || intent.ConsistencyLevel != 0
                || WttPayloadCodec.Normalize(intent.Amount.Requested, fee.ToString(CultureInfo.InvariantCulture), asset.Decimals) != intent.Amount
                || intent.RequiresAttestation && asset.OriginChain != 8)
                throw new InvalidDataException("Algorand publication normalization, nonce, consistency or attestation origin differs.");
            var owner = await ReadWttAlgorandJsonAsync("/v2/accounts/" + intent.SourceAuthority.Address, ct);
            if (WttAlgoText(owner, "address") != intent.SourceAuthority.Address || WttAlgoUnsigned(owner, "round") != snapshot.Round
                || owner.TryGetProperty("auth-addr", out var auth) && (auth.ValueKind != JsonValueKind.String || auth.GetString() != intent.SourceAuthority.Address))
                throw new InvalidDataException("Source payer is not the exact unrekeyed account at the observed round.");
            var assetId = UInt64Text.Parse(asset.TokenAddress);
            if (assetId != 0 && (!WttAlgoHoldingReady(owner, assetId) || !owner.GetProperty("assets").EnumerateArray()
                .Any(holding => WttAlgoUnsigned(holding, "asset-id") == assetId && WttAlgoUnsigned(holding, "amount") >= amount)))
                throw new InvalidDataException("Source ASA holding is absent, frozen or insufficient.");
            var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
            var minimum = WttAlgoUnsigned(parameters, "min-fee");
            var byteFee = WttAlgoUnsigned(parameters, "fee");
            if (minimum == 0 || WttAlgoUnsigned(parameters, "last-round") != snapshot.Round
                || WttAlgoText(parameters, "genesis-hash") != snapshot.Deployment.Genesis)
                throw new InvalidDataException("Algorand fee parameters differ from the source snapshot.");
            var genesisId = WttAlgoText(parameters, "genesis-id");
            var protocolFee = snapshot.Core.Get("MessageFee"u8)?.Integer
                ?? throw new InvalidDataException("Pinned core message fee is unavailable.");
            var steps = new List<WttAlgorandSourceStep>();
            var emitter = Address.ForApplication(snapshot.Deployment.Token).Bytes;
            await StorageAsync("EmitterStorage", snapshot.Deployment.Core, 0, emitter);
            var custody = WttAlgorandStorage.Address(snapshot.Deployment.Token,
                asset.OriginChain == 8 ? assetId : asset.OriginChain,
                asset.OriginChain == 8 ? "native"u8.ToArray() : Convert.FromHexString(asset.OriginAddress));
            var custodyAccount = await ReadWttAlgorandJsonAsync("/v2/accounts/" + custody, ct, allowMissing: true);
            if (asset.OriginChain == 8)
            {
                await StorageAsync("CustodyStorage", snapshot.Deployment.Token, assetId, "native"u8.ToArray(), custodyAccount);
                if (assetId != 0 && (custodyAccount.ValueKind == JsonValueKind.Undefined || !WttAlgoHoldingReady(custodyAccount, assetId)))
                {
                    if (custodyAccount.ValueKind != JsonValueKind.Undefined && custodyAccount.TryGetProperty("assets", out var holdings)
                        && holdings.EnumerateArray().Any(item => WttAlgoUnsigned(item, "asset-id") == assetId))
                        throw new InvalidDataException("Original ASA custody holding is frozen.");
                    steps.Add(new("CustodyOptIn", 0, 0, "", new("CustodyOptIn", "0", "100000", "0", intent.SourceAuthority.Address)));
                }
            }
            else if (custodyAccount.ValueKind == JsonValueKind.Undefined || !WttAlgoHoldingReady(custodyAccount, assetId))
                throw new InvalidDataException("Wrapped ASA canonical custody is unavailable.");
            var creator = assetId == 0 ? custody : WttAlgorandAsset.Read(await ReadWttAlgorandJsonAsync($"/v2/assets/{assetId}", ct), assetId).Creator;
            foreach (var stage in intent.RequiresAttestation ? new[] { "Attest", "Transfer" } : new[] { "Transfer" })
                steps.Add(new(stage, 0, 0, "", new(stage, "0", "0",
                    checked(protocolFee + (stage == "Transfer" && asset.NativeAsset ? amount : 0)).ToString(CultureInfo.InvariantCulture), intent.SourceAuthority.Address)));
            var hash = CanonicalJson.Hash(intent);
            var plan = new WttAlgorandSourcePlan(1, hash, snapshot.Deployment.Core, snapshot.Deployment.Token,
                snapshot.Deployment.Genesis, genesisId, assetId, creator, protocolFee, minimum, minimum,
                checked(minimum * (protocolFee == 0 ? 2UL : 3UL)), checked(minimum * 2), steps,
                snapshot.Deployment.CoreApproval, snapshot.Deployment.CoreClear,
                snapshot.Deployment.TokenApproval, snapshot.Deployment.TokenClear);
            plan = PriceWttAlgoSourcePlan(intent, plan, byteFee, snapshot.Round);
            RequireWttAlgoBudget(plan.Steps.Select(step => step.Reservation), intent.SourceBudget);
            var balance = WttAlgoUnsigned(owner, "amount"); var locked = WttAlgoUnsigned(owner, "min-balance");
            var spending = plan.Steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee)
                + WttPayloadCodec.ParseAmount(step.Reservation.LockedDeposit) + WttPayloadCodec.ParseAmount(step.Reservation.Value));
            if (balance < locked || spending > balance - locked) throw new InvalidDataException("Source ALGO cannot cover all disclosed prerequisites and publication costs.");
            await RequireWttAlgoRoundAsync(snapshot.Round, ct);
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            return AZOAResult<WttStageQuote>.Success(new(CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = json, ExpiresAt = expires }),
                hash, intent.Source.ProtocolIdentity, "Source", plan.Steps.Select(step => step.Reservation).ToArray(), json, expires));

            async Task StorageAsync(string stage, ulong app, ulong index, byte[] discriminator, JsonElement? known = null)
            {
                var address = WttAlgorandStorage.Address(app, index, discriminator);
                var account = known ?? await ReadWttAlgorandJsonAsync("/v2/accounts/" + address, ct, allowMissing: true);
                if (account.ValueKind != JsonValueKind.Undefined)
                {
                    if (WttAlgorandStorage.Read(account, app, address, snapshot.Round) is { } blob)
                    {
                        if (!WttAlgorandStorage.IsSourceRole(blob, stage == "EmitterStorage"))
                            throw new InvalidDataException("Storage contents do not match the immutable source role.");
                        return;
                    }
                    if (!WttAlgorandStorage.IsUnallocated(account, address, snapshot.Round))
                        throw new InvalidDataException("Storage address exists with incompatible authority or application state.");
                }
                steps.Add(new(stage, app, index, Convert.ToHexStringLower(discriminator),
                    new(stage, "0", "1002000", "0", intent.SourceAuthority.Address)));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException)
        { return AZOAResult<WttStageQuote>.Failure(exception.Message); }
    }

    private bool ValidWttAlgoSourceIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                SourceAuthority: not null, SourceBudget: not null }
            && !string.IsNullOrWhiteSpace(intent.OperationId) && intent.ActorUserId != Guid.Empty
            && intent.Source.Network == ActiveNetwork && intent.Source.ChainType == ChainType && intent.Source.ProtocolChain == 8
            && intent.Target.ProtocolChain is not (0 or 8) && ValidateAddressFormat(intent.SourceAuthority.Address)
            && intent.SourceAuthority.Context.IsResolvableUserContext && !intent.SourceAuthority.Context.IsPlatform
            && intent.SourceAuthority.Context.UserId == intent.ActorUserId && intent.SourceAuthority.Context.ChainType == ChainType
            && intent.SourceAuthority.Context.ActingTenantId == intent.TenantId.GetValueOrDefault()
            && (intent.TenantId is null || intent.SourceAuthority.Context.GrantorUserId == intent.ActorUserId)
            && intent.SourceAuthority.Context.Scope == AzoaScopes.WttSourceSign;

    private static void RequireWttAlgoBudget(IEnumerable<WttStageReservation> reservations, WttChainBudget budget)
    {
        var rows = reservations.ToArray();
        if (rows.Aggregate(BigInteger.Zero, (sum, row) => sum + WttPayloadCodec.ParseAmount(row.NetworkFee)) > WttPayloadCodec.ParseAmount(budget.MaximumNetworkFee)
            || rows.Aggregate(BigInteger.Zero, (sum, row) => sum + WttPayloadCodec.ParseAmount(row.LockedDeposit)) > WttPayloadCodec.ParseAmount(budget.MaximumLockedDeposit)
            || rows.Aggregate(BigInteger.Zero, (sum, row) => sum + WttPayloadCodec.ParseAmount(row.Value)) > WttPayloadCodec.ParseAmount(budget.MaximumValue))
            throw new InvalidDataException("Complete WTT stage reservations exceed the separately authorized source budget.");
    }
}
