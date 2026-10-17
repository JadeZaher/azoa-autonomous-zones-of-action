using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private async Task<NativeSolanaQuote> PlanSolanaAccountAsync(SolanaNativeInput input, CancellationToken ct)
    {
        var artifact = input.Artifact.Content.Deserialize<SolanaAccountArtifact>(NativeSolanaJson)!;
        var payer = new PublicKey(input.Payer); var authority = new PublicKey(input.Authority);
        var mint = new PublicKey(artifact.Mint); var owner = new PublicKey(artifact.Owner);
        var mintAccount = await ReadParsedAccountAsync(mint.Key, ct) ?? throw new ArgumentException("Token mint is absent.");
        var mintInfo = TokenInfo(mintAccount, "mint");
        switch (artifact.Operation)
        {
            case "CreateAta":
            {
                var address = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
                if (artifact.TokenAccount is not null && artifact.TokenAccount != address.Key) throw new ArgumentException("Associated token address differs.");
                var existing = await ReadParsedAccountAsync(address.Key, ct);
                if (existing is not null)
                {
                    var info = TokenInfo(existing.Value, "account");
                    if (info.GetProperty("mint").GetString() != mint.Key || info.GetProperty("owner").GetString() != owner.Key)
                        throw new ArgumentException("Existing associated token account differs from the requested owner and mint.");
                }
                var instruction = AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(payer, owner, mint);
                instruction.Data = [1];
                return new(address.Key, [NativeStage("CreateAta", await NativeRentAsync(165, ct), instruction)]);
            }
            case "CloseTokenAccount":
            {
                if (!ValidateAddressFormat(artifact.TokenAccount) || !ValidateAddressFormat(artifact.Destination)
                    || artifact.TokenAccount == artifact.Destination) throw new ArgumentException("Exact token account and refund destination are required.");
                var account = await ReadParsedAccountAsync(artifact.TokenAccount!, ct) ?? throw new ArgumentException("Token account is absent.");
                var info = TokenInfo(account, "account");
                if (info.GetProperty("mint").GetString() != mint.Key || info.GetProperty("owner").GetString() != owner.Key
                    || (info.TryGetProperty("closeAuthority", out var close) ? close.GetString() : owner.Key) != authority.Key)
                    throw new ArgumentException("Token close authority, owner or mint differs.");
                var amount = ulong.Parse(info.GetProperty("tokenAmount").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
                var native = info.TryGetProperty("isNative", out var wrapped) && wrapped.GetBoolean();
                if (amount != 0 && !(artifact.UnwrapNative && native && mint.Key == "So11111111111111111111111111111111111111112"))
                    throw new ArgumentException("A nonempty token account requires explicit wrapped SOL unwrapping.");
                var instruction = TokenProgram.CloseAccount(new PublicKey(artifact.TokenAccount!), new PublicKey(artifact.Destination!), authority, TokenProgram.ProgramIdKey);
                return new(artifact.TokenAccount!, [NativeStage("CloseTokenAccount", 0, instruction)], Disclosure: new(
                    "Close the token account and return all of its lamports to the selected destination. The final amount is recorded from the confirmed transaction.",
                    artifact.Destination, account.GetProperty("lamports").GetUInt64().ToString(CultureInfo.InvariantCulture)));
            }
            case "CreateMetadata":
            {
                var address = SolanaNftMetadata.Metadata(mint);
                if (await ReadNativeAccountAsync(address.Key, ct) is not null) throw new ArgumentException("Metadata already exists; select its explicit update operation.");
                if (!mintInfo.TryGetProperty("mintAuthority", out var mintAuthority) || mintAuthority.GetString() != authority.Key)
                    throw new ArgumentException("Metadata creation requires the current mint authority.");
                if (string.IsNullOrWhiteSpace(artifact.Name) || string.IsNullOrWhiteSpace(artifact.Uri)) throw new ArgumentException("Metadata name and URI are required.");
                var update = new PublicKey(artifact.UpdateAuthority ?? authority.Key);
                if (update.Key != authority.Key) throw new ArgumentException("Creation update authority must authorize this operation.");
                var prefix = $"AZOA:Solana:NativeMetadata:Networks:{ActiveNetwork}:";
                var guard = await ReadSolanaCostGuardAsync(prefix, ct);
                var policy = await ReadSolanaMetadataPolicyAsync(prefix, "AZOA:Solana:NativeMetadata:ReviewedExecutables", guard.RentBase64, ct);
                var rentBytes = Convert.FromBase64String(guard.RentBase64);
                var costs = policy.Costs(rentBytes);
                var guarded = NativeStage("CreateMetadata", costs.RetainedRent,
                    WttSolanaCostGuard.BuildMetadata(new PublicKey(guard.Program), rentBytes, Convert.FromHexString(policy.CodeHash)),
                    SolanaNativeMetadata.Create(mint, authority, payer, update, artifact))
                    with { ProtocolLevy = costs.ProtocolLevy.ToString(CultureInfo.InvariantCulture) };
                return new(address.Key, [guarded], Disclosure: new(
                    "Create metadata. The value allowance pays the non-refundable metadata protocol levy; locked rent is reserved separately."));
            }
            case "UpdateMetadata":
            {
                var address = SolanaNftMetadata.Metadata(mint);
                var account = await ReadNativeAccountAsync(address.Key, ct) ?? throw new ArgumentException("Metadata is absent.");
                if (account.Owner != SolanaNftMetadata.Program.Key || !IsNativeHash(artifact.ExpectedMetadataHash)
                    || NativeHash(account.Data) != artifact.ExpectedMetadataHash) throw new ArgumentException("Metadata snapshot changed.");
                var existing = SolanaNativeMetadata.Read(account.Data);
                return new(address.Key, [NativeStage("UpdateMetadata", 0, SolanaNativeMetadata.Update(mint, authority, existing, artifact))], Disclosure: new(
                    "Replace metadata with the complete values shown. The current snapshot is checked before preparation; other changes made before execution can be replaced.",
                    ResultingMetadata: JsonSerializer.SerializeToElement(new
                    {
                        name = artifact.Name ?? existing.Name, symbol = artifact.Symbol ?? existing.Symbol, uri = artifact.Uri ?? existing.Uri,
                        sellerFeeBasisPoints = artifact.SellerFeeBasisPoints ?? existing.Royalty, creators = artifact.Creators ?? existing.Creators,
                        mutable = artifact.Mutable ?? existing.Mutable, updateAuthority = artifact.UpdateAuthority ?? existing.Authority,
                        collection = existing.Collection, uses = existing.Uses,
                    })));
            }
            default: throw new ArgumentException("Unknown typed Solana account operation.");
        }
    }

    private async Task ValidateSolanaNativeStageStateAsync(SolanaNativeInput input, NativeSolanaQuote quote, NativeSolanaStage stage, CancellationToken ct)
    {
        if (stage.Name == "CloseBuffer")
        {
            await RequireRecoveryBufferAsync(stage.Instructions[0].Accounts[0].Address, input.Authority, ct);
            return;
        }
        if (stage.Name == "RemoveAuthority")
        {
            await RequireRecoveryProgramAuthorityAsync(quote, input.Authority, ct);
            return;
        }
        if (input.Operation == "Recovery")
        {
            if (stage.Name == "CloseBuffer") await RequireRecoveryBufferAsync(quote.Resource, input.Authority, ct);
            else if (stage.Name == "RemoveAuthority") await RequireRecoveryProgramAuthorityAsync(quote, input.Authority, ct);
            else throw new ArgumentException("Recovery contains an unrelated loader stage.");
            return;
        }
        if (input.Operation == "Account")
        {
            var current = await PlanSolanaAccountAsync(input, ct);
            if (current.Resource != quote.Resource || JsonSerializer.Serialize(current.Stages[0].Instructions) != JsonSerializer.Serialize(stage.Instructions)
                || ulong.Parse(current.Stages[0].Rent, CultureInfo.InvariantCulture) > ulong.Parse(stage.Rent, CultureInfo.InvariantCulture)
                || current.Stages[0].ProtocolLevy != stage.ProtocolLevy)
                throw new ArgumentException("Current Solana account effect or rent differs from the quoted intent.");
            return;
        }
        if (input.Operation == "Call")
        {
            var call = input.Artifact.Content.Deserialize<SolanaInstructionArtifact>(NativeSolanaJson)!;
            await RequireProgramHashAsync(call.ProgramId, call.ExpectedProgramDataHash!, ct);
            return;
        }
        if (stage.Name == "Upgrade") await RequireProgramHashAsync(quote.Resource, input.ExpectedProgramDataHash!, ct);
        if (stage.Name is "CreateBuffer" or "Deploy")
        {
            foreach (var instruction in stage.Instructions.Where(item => item.Program == SystemProgram.ProgramIdKey.Key))
            {
                var bytes = Convert.FromBase64String(instruction.Data);
                if (bytes.Length < 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 3) continue;
                var account = instruction.Accounts[1].Address;
                if (await ReadNativeAccountAsync(account, ct) is not null) throw new ArgumentException("Seeded creation account already exists; retain the original operation for reconciliation.");
            }
        }
    }
}
