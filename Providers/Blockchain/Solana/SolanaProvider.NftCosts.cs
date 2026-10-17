using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record NftCostEvidence(WttSolanaGuard Guard, SolanaMetadataCreationPolicy Policy,
        SolanaNftAccountCosts Accounts, string Mint, string Holding, string? Edition, string IntentHash);

    /// <summary>Recipient-stage evidence (F-M11): the conservatively reserved holding rent and the payer the message charges.</summary>
    private sealed record NftStageCostEvidence(string Stage, string Payer, string Holding, bool HoldingExists, ulong HoldingRent, string IntentHash);

    private const int NftTokenAccountLength = 165;

    private static bool IsNftStageCostKind(string kind)
        => kind is NftTransactionKinds.Accept or NftTransactionKinds.Transfer or NftTransactionKinds.ReturnForBurn;

    /// <inheritdoc/>
    public async Task<AZOAResult<NftProtocolCostEstimate>> QuoteNftProtocolCostsAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
        if (IsNftStageCostKind(intent.Kind)) return await QuoteNftStageCostsAsync(intent, ct);
        if (intent.Kind != NftTransactionKinds.Create || !ValidNftIntent(intent) || NftSigningContexts(intent) is null)
            return AZOAResult<NftProtocolCostEstimate>.Failure("NFT creation costs require the exact authenticated creation intent.");
        try
        {
            var evidence = await ReadNftCostEvidenceAsync(intent, ct);
            var plan = await NewPlanAsync(evidence.Mint, evidence.Accounts.MintRent, 0, false, IntentDigest(intent), ct);
            plan = plan with { NftCosts = evidence };
            var fee = await NativeMessageFeeAsync(CompileDurable(plan, NftPayer(intent), NftInstructions(intent, plan)), ct);
            return AZOAResult<NftProtocolCostEstimate>.Success(new("solana:" + plan.Genesis,
                intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence)),
                new(fee.ToString(CultureInfo.InvariantCulture), evidence.Accounts.RetainedRent.ToString(CultureInfo.InvariantCulture),
                    evidence.Accounts.ProtocolLevy.ToString(CultureInfo.InvariantCulture))));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return AZOAResult<NftProtocolCostEstimate>.Failure(exception.Message);
        }
    }

    private async Task<NftCostEvidence> ReadNftCostEvidenceAsync(NftTransactionIntent intent, CancellationToken ct)
    {
        var prefix = $"AZOA:Solana:Nft:Networks:{ActiveNetwork}:";
        var guard = await ReadSolanaCostGuardAsync(prefix, ct);
        var policy = await ReadSolanaMetadataPolicyAsync(prefix, "AZOA:Solana:Nft:ReviewedMetadataExecutables", guard.RentBase64, ct);
        var mint = DeriveNftMint(intent);
        if (await ReadParsedAccountAsync(mint.Key, ct) is not null)
            throw new InvalidDataException("The deterministic NFT mint already exists; reconcile its original operation.");
        var initialOwner = intent.Soulbound ? new PublicKey(CredentialAuthorityAddress!) : NftDestination(intent);
        var holding = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(initialOwner, mint);
        var accounts = policy.NftCosts(Convert.FromBase64String(guard.RentBase64), intent.Soulbound);
        return new(guard, policy, accounts, mint.Key, holding.Key, intent.Soulbound ? null : SolanaNftMetadata.Edition(mint).Key,
            IntentDigest(intent with { ProtocolCostQuote = null }));
    }

    private bool MatchesNftCostReceipt(NftTransactionIntent intent, NftCostEvidence evidence)
    {
        var quote = intent.ProtocolCostQuote;
        var authority = intent.Authorization;
        if (!NftProtocolCostContract.IsValid(quote) || authority is null || intent.Kind != NftTransactionKinds.Create ||
            quote!.ActorUserId != authority.ActorUserId || quote.ActingTenantId != authority.ActorSigningContext.ActingTenantId ||
            quote.PayerWalletId != authority.ActorSigningContext.WalletId || quote.PayerAddress != NftPayer(intent).Key ||
            quote.ChainType != intent.ChainType || quote.Network != intent.Network ||
            quote.PhysicalNetworkIdentity != "solana:" + ExpectedGenesis || quote.LifecycleId != authority.LifecycleId ||
            quote.Stage != intent.Kind || quote.PreparationGeneration != authority.PreparationGeneration ||
            quote.TokenStandard != (intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard) ||
            evidence.IntentHash != IntentDigest(intent with { ProtocolCostQuote = null }) ||
            quote.PolicyEvidenceJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence))) return false;
        var mint = DeriveNftMint(intent);
        var initialOwner = intent.Soulbound ? new PublicKey(CredentialAuthorityAddress!) : NftDestination(intent);
        var costs = evidence.Policy.NftCosts(Convert.FromBase64String(evidence.Guard.RentBase64), intent.Soulbound);
        return evidence.Accounts == costs && evidence.Mint == mint.Key &&
            evidence.Holding == AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(initialOwner, mint).Key &&
            evidence.Edition == (intent.Soulbound ? null : SolanaNftMetadata.Edition(mint).Key) &&
            NftProtocolCostContract.Covers(quote.Limits, NftCostAmounts(evidence, 0));
    }

    /// <summary>Quotes a recipient stage without custody: worst-case message fee plus rent for creating the destination holding.</summary>
    private async Task<AZOAResult<NftProtocolCostEstimate>> QuoteNftStageCostsAsync(NftTransactionIntent intent, CancellationToken ct)
    {
        if (!ValidNftIntent(intent) || NftSigningContexts(intent) is null)
            return AZOAResult<NftProtocolCostEstimate>.Failure("NFT stage costs require the exact authenticated stage intent.");
        try
        {
            var mint = new PublicKey(intent.AssetId!);
            var inspected = await InspectNftMovementAsync(intent, mint, ct);
            if (inspected.IsError) return new() { IsError = true, Message = inspected.Message, Code = inspected.Code };
            var rent = await NativeRentAsync(NftTokenAccountLength, ct);
            var plan = await NewPlanAsync(mint.Key, 0, 0, true, IntentDigest(intent), ct);
            var fee = await NativeMessageFeeAsync(CompileDurable(plan, NftPayer(intent), NftInstructions(intent, plan)), ct);
            var holding = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(NftDestination(intent), mint);
            var evidence = new NftStageCostEvidence(intent.Kind, NftPayer(intent).Key, holding.Key, !inspected.Result, rent,
                IntentDigest(intent with { ProtocolCostQuote = null }));
            return AZOAResult<NftProtocolCostEstimate>.Success(new("solana:" + plan.Genesis,
                intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence)), NftStageAmounts(fee, rent)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return AZOAResult<NftProtocolCostEstimate>.Failure(exception.Message);
        }
    }

    /// <summary>The accepted receipt binds this exact stage, generation, payer wallet and physical network.</summary>
    private bool MatchesNftStageCostReceipt(NftTransactionIntent intent)
    {
        var quote = intent.ProtocolCostQuote;
        var authority = intent.Authorization;
        if (!IsNftStageCostKind(intent.Kind) || !NftProtocolCostContract.IsValid(quote) || authority is null) return false;
        var payer = intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.ReturnForBurn ? intent.SigningContext : authority.ActorSigningContext;
        return quote!.ActorUserId == payer.UserId && quote.PayerWalletId == payer.WalletId && quote.PayerAddress == NftPayer(intent).Key
            && quote.ChainType == intent.ChainType && quote.Network == intent.Network
            && quote.PhysicalNetworkIdentity == "solana:" + ExpectedGenesis && quote.LifecycleId == authority.LifecycleId
            && quote.Stage == intent.Kind && quote.PreparationGeneration == authority.PreparationGeneration
            && quote.TokenStandard == (intent.Soulbound ? SolanaNftMetadata.CredentialStandard : SolanaNftMetadata.MasterEditionStandard);
    }

    private static NftProtocolCostLimits NftStageAmounts(ulong networkFee, ulong holdingRent)
        => new(networkFee.ToString(CultureInfo.InvariantCulture), holdingRent.ToString(CultureInfo.InvariantCulture), "0");

    /// <summary>Reads the recorded asset, source and destination holdings; the result says whether the destination must be created.</summary>
    private async Task<AZOAResult<bool>> InspectNftMovementAsync(NftTransactionIntent intent, PublicKey mint, CancellationToken ct)
    {
        var asset = await ReadNftAssetAsync(mint, ct);
        if (asset is null || asset.Metadata.Uri != intent.MetadataUri || asset.Metadata.Name != intent.Name
            || asset.Credential != intent.Soulbound || (intent.Soulbound && asset.Metadata.Issuer != intent.Authorization!.IssuerAddress))
            return AZOAResult<bool>.Failure("NFT mint or immutable metadata does not match recorded issuance.");
        var destinationOwner = NftDestination(intent);
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(destinationOwner, mint);
        var destinationAccount = await ReadParsedAccountAsync(destination.Key, ct);
        if (intent.Kind == NftTransactionKinds.Accept)
        {
            if (destinationOwner.Key != intent.SignerAddress) return AZOAResult<bool>.Failure("Acceptance requires the destination owner's signature.");
            if (destinationAccount is not null && !ValidHolding(destinationAccount.Value, destinationOwner.Key, mint.Key, false, out _))
                return AZOAResult<bool>.Failure("Destination account cannot accept the recorded NFT.");
            return AZOAResult<bool>.Success(destinationAccount is null);
        }
        var sourceOwner = NftSource(intent);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(sourceOwner, mint);
        var sourceAccount = await ReadParsedAccountAsync(source.Key, ct);
        var frozenSource = intent.Soulbound && intent.Kind == NftTransactionKinds.Revoke;
        if (sourceAccount is null || !ValidHolding(sourceAccount.Value, sourceOwner.Key, mint.Key, frozenSource, out var balance) || balance != 1)
            return AZOAResult<bool>.Failure("Recorded NFT source does not hold the single token.");
        if (frozenSource && !HasIssuerDelegate(sourceAccount.Value, intent.Authorization!.IssuerAddress!))
            return AZOAResult<bool>.Failure("Frozen credential does not retain its immutable issuer delegate.");
        if (intent.Kind != NftTransactionKinds.Burn && intent.Soulbound
            && (destinationAccount is null || !ValidHolding(destinationAccount.Value, destinationOwner.Key, mint.Key, true, out var destinationBalance)
                || destinationBalance != 0 || !HasIssuerDelegate(destinationAccount.Value, intent.Authorization!.IssuerAddress!)))
            return AZOAResult<bool>.FailureWithCode("The destination must authorize its frozen credential account again.", "NFT_RECIPIENT_ACCEPTANCE_REQUIRED");
        if (!intent.Soulbound && destinationAccount is not null
            && !ValidHolding(destinationAccount.Value, destinationOwner.Key, mint.Key, false, out _))
            return AZOAResult<bool>.Failure("NFT destination account is incompatible.");
        return AZOAResult<bool>.Success(destinationAccount is null);
    }

    private static NftProtocolCostLimits NftCostAmounts(NftCostEvidence evidence, ulong networkFee)
        => new(networkFee.ToString(CultureInfo.InvariantCulture), evidence.Accounts.RetainedRent.ToString(CultureInfo.InvariantCulture),
            evidence.Accounts.ProtocolLevy.ToString(CultureInfo.InvariantCulture));
}
