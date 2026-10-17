using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Configuration;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : IWttSourceModule
{
    /// <inheritdoc/>
    public Task<AZOAResult<WttPublicationRules>> ReadWttPublicationRulesAsync(CancellationToken ct = default)
        => Task.FromResult(ReadWttSolanaDeployment() is null
            ? Error<WttPublicationRules>("An enabled pinned WTT deployment is required.")
            : Ok(new WttPublicationRules(32, false)));

    /// <inheritdoc cref="IWttSourceModule.QuoteWttSourceAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttSourceAsync(WttOperationIntent intent, CancellationToken ct = default)
    {
        if (!ValidWttSourceIntent(intent)) return Error<WttStageQuote>("Exact actor, deployment and source custody are required.");
        try
        {
            var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("An enabled pinned WTT deployment is required.");
            var mint = new PublicKey(intent.Asset.TokenAddress); var owner = new PublicKey(intent.SourceAuthority.Address);
            var emitter = WttSolanaAccounts.Derive(deployment.TokenBridge, "emitter"u8.ToArray());
            var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
            var snapshot = await ReadWttSolanaSnapshotAsync(deployment,
                [mint, WttSolanaAccounts.Derive(deployment.TokenBridge, "meta"u8.ToArray(), mint.KeyBytes), SolanaNftMetadata.Metadata(mint),
                 source, WttSolanaAccounts.Derive(deployment.TokenBridge, mint.KeyBytes),
                 WttSolanaAccounts.Derive(deployment.Core, "Sequence"u8.ToArray(), emitter.KeyBytes)], ct);
            if (WttDescriptor(snapshot) != intent.Source || ReadWttSolanaAsset(snapshot, mint, intent.Asset.NativeAsset) != intent.Asset)
                throw new InvalidDataException("Actual source deployment or asset differs from the immutable intent.");
            var amount = UInt64Text.Parse(intent.Amount.Debited);
            var denormalizedFee = WttPayloadCodec.ParseAmount(intent.Amount.Fee) * BigInteger.Pow(10, Math.Max(0, intent.Asset.Decimals - 8));
            if (WttPayloadCodec.Normalize(intent.Amount.Requested, denormalizedFee.ToString(CultureInfo.InvariantCulture), intent.Asset.Decimals) != intent.Amount
                || amount == 0 || denormalizedFee > ulong.MaxValue || intent.ConsistencyLevel != 32)
                throw new InvalidDataException("Source normalization or actual finalized publication consistency differs.");
            if (intent.RequiresAttestation && intent.Asset.OriginChain != 1)
                throw new InvalidDataException("Wrapped assets require their original-chain metadata attestation.");
            var createAta = snapshot.Accounts[3].ValueKind == JsonValueKind.Null;
            if (createAta && !intent.Asset.NativeAsset) throw new InvalidDataException("Source token account is absent.");
            if (!createAta)
            {
                var holding = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(snapshot.Accounts[3], TokenProgram.ProgramIdKey.Key, false));
                if (holding.Mint != mint.Key || holding.Owner != owner.Key || holding.Frozen || holding.Delegate is not null
                    || (intent.Asset.NativeAsset ? !holding.Native : holding.Amount < amount))
                    throw new InvalidDataException("Source holding is unavailable, delegated, frozen or insufficient for the exact transfer.");
            }
            var info = SolanaSplAssetCodec.ReadMint(SolanaAccountData.ReadOwned(snapshot.Accounts[0], TokenProgram.ProgramIdKey.Key, false));
            if (intent.Asset.OriginChain == 1 && info.MintAuthority == WttSolanaAccounts.Derive(deployment.TokenBridge, "mint_signer"u8.ToArray()).Key)
                throw new InvalidDataException("Bridge-controlled wrapped mint cannot be represented as an original asset.");
            var guard = await ReadWttSolanaGuardAsync(ct);
            var prefix = $"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:";
            if (!uint.TryParse(_config[prefix + "MaximumComputeUnits"], NumberStyles.None, CultureInfo.InvariantCulture, out var computeCeiling) || computeCeiling == 0
                || !UInt64Text.TryParse(_config[prefix + "MicroLamportsPerComputeUnit"], out var computePrice))
                throw new InvalidDataException("An explicit bounded WTT compute policy is required.");
            var tables = await ReadLookupTablesAsync(_config.GetSection(prefix + "LookupTables").Get<string[]>() ?? [], snapshot.Slot, ct);
            var hash = CanonicalJson.Hash(intent); var steps = new List<WttSolanaSourceStep>();
            var plan = new WttSolanaSourcePlan(1, hash, owner.Key, deployment.Core.Key, deployment.TokenBridge.Key,
                deployment.CoreHash, deployment.TokenHash, guard, snapshot.MessageFee, tables, steps);
            var rent = Convert.FromBase64String(guard.RentBase64);
            var sequenceRent = snapshot.Accounts[5].ValueKind == JsonValueKind.Null ? WttSolanaCostGuard.MinimumBalance(rent, 8) : 0;
            if (sequenceRent == 0 && SolanaAccountData.ReadOwned(snapshot.Accounts[5], deployment.Core.Key, false).Length != 8)
                throw new InvalidDataException("Emitter sequence account layout differs from the pinned core.");
            var custodyRent = 0UL;
            if (intent.Asset.OriginChain == 1)
            {
                if (snapshot.Accounts[4].ValueKind == JsonValueKind.Null) custodyRent = WttSolanaCostGuard.MinimumBalance(rent, 165);
                else
                {
                    var custody = SolanaSplAssetCodec.ReadHolding(SolanaAccountData.ReadOwned(snapshot.Accounts[4], TokenProgram.ProgramIdKey.Key, false));
                    if (custody.Mint != mint.Key || custody.Owner != WttSolanaAccounts.Derive(deployment.TokenBridge, "custody_signer"u8.ToArray()).Key || custody.Frozen)
                        throw new InvalidDataException("Original token custody account differs from the pinned bridge.");
                }
            }
            var recovery = await NewPlanAsync("", 0, 0, false, hash, ct);
            foreach (var stage in intent.RequiresAttestation ? new[] { "Attest", "Transfer" } : new[] { "Transfer" })
            {
                var publicationRent = WttSolanaCostGuard.MinimumBalance(rent, stage == "Attest" ? 195 : 228);
                var bridgeRent = checked(WttSolanaCostGuard.MinimumBalance(rent, 0) + publicationRent + sequenceRent + (stage == "Transfer" ? custodyRent : 0));
                var ataRent = stage == "Transfer" && createAta ? WttSolanaCostGuard.MinimumBalance(rent, 165) : 0;
                var funding = checked(snapshot.MessageFee + bridgeRent);
                var value = checked(snapshot.MessageFee + (stage == "Transfer" && intent.Asset.NativeAsset ? amount : 0));
                var step = new WttSolanaSourceStep(stage, funding, stage == "Transfer" && createAta, computeCeiling, computePrice,
                    new(stage, "0", checked(bridgeRent + ataRent).ToString(CultureInfo.InvariantCulture), value.ToString(CultureInfo.InvariantCulture), owner.Key));
                var bridgePayer = WttQuotePlaceholder(intent.OperationId, stage, "payer");
                var message = WttQuotePlaceholder(intent.OperationId, stage, "message");
                var trial = CompileWttSource(intent, plan, step, recovery, bridgePayer, message);
                var measured = await MeasureSolanaComputeAsync(trial, checked((long)snapshot.Slot), ct);
                var bounded = checked(measured + Math.Max(1000UL, measured / 10));
                if (bounded > computeCeiling) throw new InvalidDataException("Measured guarded WTT compute exceeds the explicit policy ceiling.");
                step = step with { ComputeUnits = checked((uint)bounded) };
                var compiled = CompileWttSource(intent, plan, step, recovery, bridgePayer, message);
                _ = await MeasureSolanaComputeAsync(compiled, checked((long)snapshot.Slot), ct);
                var networkFee = await NativeMessageFeeAsync(compiled, ct);
                step = step with { Reservation = step.Reservation with { NetworkFee = networkFee.ToString(CultureInfo.InvariantCulture) } };
                steps.Add(step);
                var cleanup = CompileDurable(recovery, owner, [SystemProgram.Transfer(bridgePayer, owner, 1)]);
                var cleanupFee = await NativeMessageFeeAsync(cleanup, ct);
                steps.Add(new("Cleanup:" + stage, 0, false, 0, 0,
                    new("Cleanup:" + stage, cleanupFee.ToString(CultureInfo.InvariantCulture), "0", "0", owner.Key)));
            }
            void RequireCap(Func<WttStageReservation, string> selector, string maximum)
            {
                if (steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(selector(step.Reservation))) > WttPayloadCodec.ParseAmount(maximum))
                    throw new InvalidDataException("Complete source publication and cleanup reservations exceed the caller's immutable budget.");
            }
            RequireCap(value => value.NetworkFee, intent.SourceBudget.MaximumNetworkFee);
            RequireCap(value => value.LockedDeposit, intent.SourceBudget.MaximumLockedDeposit);
            RequireCap(value => value.Value, intent.SourceBudget.MaximumValue);
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)); var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            var quoteId = CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = json, ExpiresAt = expires });
            return Ok(new WttStageQuote(quoteId, hash, intent.Source.ProtocolIdentity, "Source", steps.Select(value => value.Reservation).ToArray(), json, expires));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { return Error<WttStageQuote>(exception.Message); }
    }

    private bool ValidWttSourceIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                SourceAuthority: not null, SourceBudget: not null }
            && !string.IsNullOrWhiteSpace(intent.OperationId) && intent.ActorUserId != Guid.Empty
            && intent.Source.Network == ActiveNetwork && intent.Source.ChainType == ChainType && intent.Source.ProtocolChain == 1
            && intent.Target.ProtocolChain is not (0 or 1) && SolanaAddress.IsSigner(intent.SourceAuthority.Address)
            && intent.SourceAuthority.Context.IsResolvableUserContext && !intent.SourceAuthority.Context.IsPlatform
            && intent.SourceAuthority.Context.UserId == intent.ActorUserId && intent.SourceAuthority.Context.ChainType == ChainType
            && intent.SourceAuthority.Context.ActingTenantId == intent.TenantId.GetValueOrDefault()
            && (intent.TenantId is null || intent.SourceAuthority.Context.GrantorUserId == intent.ActorUserId)
            && intent.SourceAuthority.Context.Scope == AzoaScopes.WttSourceSign;

    private static PublicKey WttQuotePlaceholder(string operation, string stage, string role)
        => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("azoa:wtt:quote-only:" + operation + ":" + stage + ":" + role)));
}
