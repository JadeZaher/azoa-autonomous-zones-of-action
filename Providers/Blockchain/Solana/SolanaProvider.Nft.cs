using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc/>
    public string? CredentialAuthorityAddress => _config["AZOA:Solana:CredentialAuthorityAddress"];
    /// <inheritdoc/>
    public string NftTokenStandard => SolanaNftMetadata.MasterEditionStandard;
    /// <inheritdoc/>
    public string NativeCurrencyCode => "SOL";
    /// <inheritdoc/>
    public int NativeCurrencyDecimals => 9;

    /// <inheritdoc/>
    public async Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        if (!ValidNftIntent(intent)) return AZOAResult<PreparedNftTransaction>.Failure("Invalid Solana NFT authorization or metadata.");
        var signingContexts = NftSigningContexts(intent);
        if (signingContexts is null) return AZOAResult<PreparedNftTransaction>.Failure("One Solana public key cannot replace another participant's custody consent.");
        var mint = intent.Kind == NftTransactionKinds.Create ? DeriveNftMint(intent) : new PublicKey(intent.AssetId!);
        ulong rent = 0;
        ulong stageRent = 0;
        NftCostEvidence? costs = null;
        var createDestination = false;
        if (intent.Kind == NftTransactionKinds.Create)
        {
            if (!NftProtocolCostContract.IsValid(intent.ProtocolCostQuote))
                return AZOAResult<PreparedNftTransaction>.Failure("Solana NFT creation requires an accepted protocol cost receipt.");
            try
            {
                costs = await ReadNftCostEvidenceAsync(intent, ct);
                if (!MatchesNftCostReceipt(intent, costs))
                    return AZOAResult<PreparedNftTransaction>.Failure("The selected NFT cost receipt no longer matches this preparation or its policy.");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return AZOAResult<PreparedNftTransaction>.Failure(exception.Message);
            }
            rent = costs.Accounts.MintRent;
        }
        else
        {
            var inspected = await InspectNftMovementAsync(intent, mint, ct);
            if (inspected.IsError) return new() { IsError = true, Message = inspected.Message, Code = inspected.Code };
            createDestination = inspected.Result;
            if (IsNftStageCostKind(intent.Kind))
            {
                if (!MatchesNftStageCostReceipt(intent))
                    return AZOAResult<PreparedNftTransaction>.Failure("This Solana NFT stage requires its paying participant's accepted cost receipt.");
                stageRent = createDestination ? await NativeRentAsync(NftTokenAccountLength, ct) : 0;
            }
        }
        var plan = await NewPlanAsync(mint.Key, rent, 0, createDestination, IntentDigest(intent), ct);
        plan = plan with { NftCosts = costs };
        var instructions = NftInstructions(intent, plan);
        var message = CompileDurable(plan, NftPayer(intent), instructions);
        if (costs is not null)
        {
            var networkFee = await NativeMessageFeeAsync(message, ct);
            if (!NftProtocolCostContract.Covers(intent.ProtocolCostQuote!.Limits, NftCostAmounts(costs, networkFee)))
                return AZOAResult<PreparedNftTransaction>.Failure("NFT network fee exceeds its accepted independent limit.");
        }
        else if (IsNftStageCostKind(intent.Kind)
            && !NftProtocolCostContract.Covers(intent.ProtocolCostQuote!.Limits, NftStageAmounts(await NativeMessageFeeAsync(message, ct), stageRent)))
            return AZOAResult<PreparedNftTransaction>.Failure("NFT stage costs exceed the paying participant's accepted limits; review a new quote.");
        var signed = await SignDurableAsync(message, signingContexts, ct);
        if (signed.IsError || signed.Result is null) return AZOAResult<PreparedNftTransaction>.Failure(signed.Message);
        if (signed.Result.Length > 1232) return AZOAResult<PreparedNftTransaction>.Failure("Atomic Solana NFT transaction exceeds the network packet limit.");
        var txid = Encoders.Base58.EncodeData(Transaction.Deserialize(signed.Result).Signatures[0].Signature);
        return AZOAResult<PreparedNftTransaction>.Success(new(txid, Convert.ToBase64String(signed.Result), intent,
            "solana:" + plan.Genesis, SolanaNftMetadata.Program.Key, RecoveryMetadata: JsonSerializer.Serialize(plan),
            TokenStandard: intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard));
    }

    /// <inheritdoc/>
    public async Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var plan = ValidatePreparedNft(prepared);
        if (plan.NftCosts is { } costs)
        {
            var fee = await NativeMessageFeeAsync(CompileDurable(plan, NftPayer(prepared.Intent), NftInstructions(prepared.Intent, plan)), ct);
            if (!NftProtocolCostContract.Covers(prepared.Intent.ProtocolCostQuote!.Limits, NftCostAmounts(costs, fee)))
                throw new InvalidOperationException("The exact NFT message exceeds its accepted network fee limit.");
        }
        else if (prepared.Intent.ProtocolCostQuote is { } stageQuote)
        {
            var fee = await NativeMessageFeeAsync(CompileDurable(plan, NftPayer(prepared.Intent), NftInstructions(prepared.Intent, plan)), ct);
            var holdingRent = plan.CreateDestination ? await NativeRentAsync(NftTokenAccountLength, ct) : 0;
            if (!NftProtocolCostContract.Covers(stageQuote.Limits, NftStageAmounts(fee, holdingRent)))
                throw new InvalidOperationException("The exact NFT stage message exceeds its accepted cost limits.");
        }
        await SubmitDurableAsync(prepared.SignedTransactionBase64, prepared.TransactionId, plan, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default)
    {
        var plan = ValidatePreparedNft(prepared);
        var status = await ObserveDurableAsync(prepared.TransactionId, prepared.SignedTransactionBase64, plan, ct);
        return AZOAResult<NftTransactionObservation>.Success(new(status, prepared.TransactionId, plan.Mint,
            NftDestination(prepared.Intent).Key, prepared.ProtocolIdentity, prepared.ContractAddress));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress,
        string protocolIdentity, CancellationToken ct = default, string contractAddress = "")
    {
        if (!TryParsePublicKey(assetId, out var mint) || !TryParsePublicKey(ownerAddress, out var owner)
            || contractAddress != SolanaNftMetadata.Program.Key || protocolIdentity != "solana:" + await VerifyGenesisAsync(ct))
            return AZOAResult<NftOwnershipObservation>.Failure("Solana NFT identity does not match its network and metadata program.");
        var asset = await ReadNftAssetAsync(mint, ct);
        if (asset is null) return AZOAResult<NftOwnershipObservation>.Success(new(false, null, false));
        var account = await ReadParsedAccountAsync(AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint).Key, ct);
        if (account is null) return AZOAResult<NftOwnershipObservation>.Success(new(false, null, asset.Credential));
        var held = ValidHolding(account.Value, owner.Key, mint.Key, asset.Credential, out var amount) && amount == 1
            && (!asset.Credential || HasIssuerDelegate(account.Value, asset.Metadata.Issuer));
        return AZOAResult<NftOwnershipObservation>.Success(new(held, held ? owner.Key : null, asset.Credential));
    }

    private bool ValidNftIntent(NftTransactionIntent intent) => intent.ChainType == ChainType && intent.Network == ActiveNetwork
        && NftTransactionAuthorization.IsValid(intent) && TryParsePublicKey(intent.SignerAddress, out _)
        && TryParsePublicKey(intent.RecipientAddress, out _) && TryParsePublicKey(intent.Authorization!.ActorAddress, out _)
        && Encoding.UTF8.GetByteCount(intent.Name) is > 0 and <= 32 && Encoding.UTF8.GetByteCount(intent.MetadataUri) is > 0 and <= 200
        && (intent.Kind == NftTransactionKinds.Create ? intent.AssetId is null : TryParsePublicKey(intent.AssetId, out _))
        && (intent.ContractAddress.Length == 0 || intent.ContractAddress == SolanaNftMetadata.Program.Key)
        && (!intent.Soulbound || (TryParsePublicKey(CredentialAuthorityAddress, out _) && TryParsePublicKey(intent.Authorization.IssuerAddress, out _)))
        && (intent.Soulbound
            ? intent.Kind == NftTransactionKinds.Accept
                ? intent.SigningContext.IsResolvableUserContext && intent.SignerAddress == intent.RecipientAddress
                : intent.Kind is NftTransactionKinds.Create or NftTransactionKinds.Deliver or NftTransactionKinds.Revoke
                    && intent.SigningContext.IsPlatform && intent.SignerAddress == CredentialAuthorityAddress
            : intent.SigningContext.IsResolvableUserContext && intent.Kind != NftTransactionKinds.Deliver)
        && (intent.Fee is null || (intent.Fee.Quote.Currency == "SOL" && intent.Fee.Quote.Decimals == 9
            && CanonicalAmount(intent.Fee.Quote.NativeBaseUnits, out _) && TryParsePublicKey(intent.Fee.Quote.TreasuryAddress, out _)));

    private DurableSolanaPlan ValidatePreparedNft(PreparedNftTransaction prepared)
    {
        _ = RpcHttpClient;
        var plan = ReadPlan(prepared.RecoveryMetadata);
        if (!ValidNftIntent(prepared.Intent) || NftSigningContexts(prepared.Intent) is null || plan is null || plan.IntentHash != IntentDigest(prepared.Intent)
            || plan.Genesis != ExpectedGenesis || prepared.ProtocolIdentity != "solana:" + plan.Genesis
            || prepared.ContractAddress != SolanaNftMetadata.Program.Key || prepared.AdditionalSignedTransactionsBase64 is { Count: > 0 }
            || prepared.AtomicGroupIdentity is not null || plan.Decimals != 0 || plan.LastValidBlockHeight <= 0 || plan.ContextSlot < 0
            || plan.Mint != (prepared.Intent.Kind == NftTransactionKinds.Create ? DeriveNftMint(prepared.Intent).Key : prepared.Intent.AssetId)
            || prepared.TokenStandard != (prepared.Intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard)
            || (plan.NftCosts is null ? prepared.Intent.ProtocolCostQuote is not null && !MatchesNftStageCostReceipt(prepared.Intent) :
                !MatchesNftCostReceipt(prepared.Intent, plan.NftCosts) || plan.MintRent != plan.NftCosts.Accounts.MintRent)
            || !MatchesDurableWire(prepared.SignedTransactionBase64, prepared.TransactionId,
                CompileDurable(plan, NftPayer(prepared.Intent), NftInstructions(prepared.Intent, plan)), out _))
            throw new InvalidOperationException("Persisted Solana NFT transaction differs from its authorized intent.");
        return plan;
    }

    private static string MintSeed(NftTransactionIntent intent) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"azoa:nft:mint:{intent.Network}:{intent.SignerAddress}:{intent.PreparationKey}"))).ToLowerInvariant()[..32];
    private static PublicKey DeriveNftMint(NftTransactionIntent intent)
    {
        if (!PublicKey.TryCreateWithSeed(new PublicKey(intent.SignerAddress), MintSeed(intent), TokenProgram.ProgramIdKey, out var mint))
            throw new InvalidOperationException("Solana deterministic mint derivation failed.");
        return mint;
    }
    private static PublicKey NftPayer(NftTransactionIntent intent) => new(intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.ReturnForBurn
        ? intent.SignerAddress : intent.Authorization!.ActorAddress);
    private PublicKey NftSource(NftTransactionIntent intent) => new(intent.Kind == NftTransactionKinds.Revoke
        ? intent.SourceAddress! : intent.Kind == NftTransactionKinds.Deliver ? CredentialAuthorityAddress! : intent.SignerAddress);
    private static PublicKey NftDestination(NftTransactionIntent intent) => new(intent.RecipientAddress);

    private Dictionary<string, SigningContext>? NftSigningContexts(NftTransactionIntent intent)
    {
        var roles = new List<(string Address, SigningContext Context)> { (intent.SignerAddress, intent.SigningContext) };
        if (intent.Kind is not (NftTransactionKinds.Accept or NftTransactionKinds.ReturnForBurn))
            roles.Add((intent.Authorization!.ActorAddress, intent.Authorization.ActorSigningContext));
        if (intent.Soulbound)
        {
            var grant = intent.Authorization!;
            roles.Add((CredentialAuthorityAddress!, grant.ActorSigningContext with { IsPlatform = true, UserId = Guid.Empty, WalletId = Guid.Empty, ChainType = ChainType }));
            if (intent.Kind == NftTransactionKinds.Create || intent.Kind == NftTransactionKinds.Revoke)
                roles.Add((grant.IssuerAddress!, grant.IssuerSigningContext!.Value));
        }
        var contexts = new Dictionary<string, SigningContext>();
        foreach (var role in roles)
        {
            var context = role.Context.ForChain(ChainType);
            if (contexts.TryGetValue(role.Address, out var existing) && existing != context) return null;
            contexts[role.Address] = context;
        }
        return contexts;
    }

    private static bool HasIssuerDelegate(JsonElement account, string issuer)
    {
        var info = TokenInfo(account, "account");
        return info.TryGetProperty("delegate", out var delegated) && delegated.GetString() == issuer
            && info.TryGetProperty("delegatedAmount", out var amount) && amount.GetProperty("amount").GetString() == "1";
    }
}
