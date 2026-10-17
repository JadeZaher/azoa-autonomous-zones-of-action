using System.Text.Json;
using AZOA.WebAPI.Models.Blockchain;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private List<TransactionInstruction> NftInstructions(NftTransactionIntent intent, DurableSolanaPlan plan)
    {
        var instructions = new List<TransactionInstruction>();
        var mint = new PublicKey(plan.Mint); var signer = new PublicKey(intent.SignerAddress);
        var payer = NftPayer(intent); var recipient = NftDestination(intent);
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(recipient, mint);
        var platform = intent.Soulbound ? new PublicKey(CredentialAuthorityAddress!) : signer;
        if (intent.Kind == NftTransactionKinds.Create)
        {
            if (plan.NftCosts is { } costs)
                instructions.Add(WttSolanaCostGuard.BuildMetadata(new PublicKey(costs.Guard.Program),
                    Convert.FromBase64String(costs.Guard.RentBase64), Convert.FromHexString(costs.Policy.CodeHash)));
            var initialOwner = intent.Soulbound ? platform : recipient;
            var holding = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(initialOwner, mint);
            instructions.Add(SystemProgram.CreateAccountWithSeed(payer, mint, signer, MintSeed(intent), plan.MintRent,
                (ulong)TokenProgram.MintAccountDataSize, TokenProgram.ProgramIdKey));
            instructions.Add(TokenProgram.InitializeMint(mint, 0, signer, signer));
            instructions.Add(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(payer, initialOwner, mint));
            instructions.Add(TokenProgram.MintTo(mint, holding, 1, signer));
            instructions.Add(SolanaNftMetadata.Create(mint, signer, payer, new PublicKey(intent.Authorization!.ActorAddress), intent.Name, intent.MetadataUri));
            if (intent.Soulbound) instructions.Add(new TransactionInstruction { ProgramId = TokenProgram.ProgramIdKey.KeyBytes, Keys = new List<AccountMeta> { AccountMeta.Writable(mint, false), AccountMeta.ReadOnly(signer, true) }, Data = new byte[] { 6, 0, 0 } });
            else instructions.Add(SolanaNftMetadata.CreateEdition(mint, signer, payer));
        }
        else if (intent.Kind == NftTransactionKinds.Accept)
        {
            if (plan.CreateDestination) instructions.Add(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(payer, recipient, mint));
            if (intent.Soulbound)
            {
                instructions.Add(TokenProgram.Approve(destination, new PublicKey(intent.Authorization!.IssuerAddress!), recipient, 1));
                instructions.Add(TokenProgram.FreezeAccount(destination, mint, platform, TokenProgram.ProgramIdKey));
            }
        }
        else
        {
            var sourceOwner = NftSource(intent);
            var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(sourceOwner, mint);
            if (intent.Kind == NftTransactionKinds.Burn)
                instructions.Add(TokenProgram.Burn(source, mint, 1, signer));
            else if (intent.Soulbound)
            {
                if (intent.Kind == NftTransactionKinds.Revoke) instructions.Add(TokenProgram.ThawAccount(source, mint, platform, TokenProgram.ProgramIdKey));
                instructions.Add(TokenProgram.ThawAccount(destination, mint, platform, TokenProgram.ProgramIdKey));
                var transferAuthority = intent.Kind == NftTransactionKinds.Revoke ? new PublicKey(intent.Authorization!.IssuerAddress!) : platform;
                instructions.Add(TokenProgram.TransferChecked(source, destination, 1, 0, transferAuthority, mint));
                instructions.Add(TokenProgram.FreezeAccount(destination, mint, platform, TokenProgram.ProgramIdKey));
                if (intent.Kind == NftTransactionKinds.Revoke) instructions.Add(TokenProgram.FreezeAccount(source, mint, platform, TokenProgram.ProgramIdKey));
            }
            else
            {
                if (plan.CreateDestination) instructions.Add(AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(payer, recipient, mint));
                instructions.Add(TokenProgram.TransferChecked(source, destination, 1, 0, signer, mint));
            }
        }
        if (intent.Fee is { } fee)
            instructions.Add(SystemProgram.Transfer(new PublicKey(fee.PayerAddress), new PublicKey(fee.Quote.TreasuryAddress),
                ulong.Parse(fee.Quote.NativeBaseUnits, System.Globalization.CultureInfo.InvariantCulture)));
        var proofSigner = intent.Kind is NftTransactionKinds.Accept or NftTransactionKinds.ReturnForBurn ? signer : new PublicKey(intent.Authorization!.ActorAddress);
        instructions.Add(MemoProgram.NewMemoV2("azoa:nft:" + plan.IntentHash, proofSigner));
        return instructions;
    }

    private sealed record SolanaNftAsset(SolanaNftMetadata.MetadataEvidence Metadata, bool Credential);
    private async Task<SolanaNftAsset?> ReadNftAssetAsync(PublicKey mint, CancellationToken ct)
    {
        var mintAccount = await ReadParsedAccountAsync(mint.Key, ct);
        if (mintAccount is null) return null;
        var info = TokenInfo(mintAccount.Value, "mint");
        if (!info.GetProperty("isInitialized").GetBoolean() || info.GetProperty("decimals").GetInt32() != 0
            || info.GetProperty("supply").GetString() != "1") return null;
        var metadataAccount = await ReadParsedAccountAsync(SolanaNftMetadata.Metadata(mint).Key, ct);
        if (metadataAccount is null || metadataAccount.Value.GetProperty("owner").GetString() != SolanaNftMetadata.Program.Key
            || metadataAccount.Value.GetProperty("executable").GetBoolean()) return null;
        var metadata = SolanaNftMetadata.Read(Convert.FromBase64String(metadataAccount.Value.GetProperty("data")[0].GetString()!));
        if (metadata.Mint != mint.Key || !metadata.Immutable || !metadata.VerifiedCreator) return null;
        var mintAuthority = info.GetProperty("mintAuthority"); var freezeAuthority = info.GetProperty("freezeAuthority");
        if (mintAuthority.ValueKind == JsonValueKind.Null && freezeAuthority.GetString() == CredentialAuthorityAddress
            && !string.IsNullOrWhiteSpace(CredentialAuthorityAddress)) return new(metadata, true);
        var editionKey = SolanaNftMetadata.Edition(mint);
        if (mintAuthority.GetString() != editionKey.Key || freezeAuthority.GetString() != editionKey.Key) return null;
        var edition = await ReadParsedAccountAsync(editionKey.Key, ct);
        if (edition is null || edition.Value.GetProperty("owner").GetString() != SolanaNftMetadata.Program.Key) return null;
        var editionBytes = Convert.FromBase64String(edition.Value.GetProperty("data")[0].GetString()!);
        if (editionBytes.Length < 18 || editionBytes[0] != 6 || editionBytes[9] != 1
            || editionBytes.AsSpan(1, 8).IndexOfAnyExcept((byte)0) >= 0 || editionBytes.AsSpan(10, 8).IndexOfAnyExcept((byte)0) >= 0) return null;
        return new(metadata, false);
    }
}
