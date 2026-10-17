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

    /// <inheritdoc/>
    public async Task<AZOAResult<NftProtocolCostEstimate>> QuoteNftProtocolCostsAsync(NftTransactionIntent intent, CancellationToken ct = default)
    {
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

    private static NftProtocolCostLimits NftCostAmounts(NftCostEvidence evidence, ulong networkFee)
        => new(networkFee.ToString(CultureInfo.InvariantCulture), evidence.Accounts.RetainedRent.ToString(CultureInfo.InvariantCulture),
            evidence.Accounts.ProtocolLevy.ToString(CultureInfo.InvariantCulture));
}
