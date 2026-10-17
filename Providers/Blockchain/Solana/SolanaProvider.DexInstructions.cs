using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterCompilation(byte[] Message, IReadOnlyList<SolanaLookupTableSnapshot> LookupTables,
        IReadOnlyList<JupiterInstruction> Instructions, JupiterNativeAccount? NativeAccount);

    private static JupiterCompilation CompileJupiter(DexQuoteTerms terms, JupiterBuildPlan build,
        DurableSolanaPlan recovery, string preparationKey, uint computeUnits, ulong computePrice,
        ulong tokenAccountRent, JupiterState state, string? effectIdentity = null)
    {
        if (terms.Owner != terms.Payer || terms.Owner.Address != terms.RecipientAddress ||
            terms.InputAsset.Native && terms.OutputAsset.Native || computeUnits is 0 or > 1_400_000)
            throw new InvalidDataException("Jupiter compilation changed the exact owner or native lifecycle.");
        var owner = new PublicKey(terms.Owner.Address);
        var inputMint = new PublicKey(build.InputMint);
        var outputMint = new PublicKey(build.OutputMint);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, inputMint).Key;
        var destination = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, outputMint).Key;
        if (state.BuildHash != CanonicalJson.Hash(build) || state.Roles.Signer != owner.Key ||
            state.Source != source || state.Destination != destination)
            throw new InvalidDataException("Jupiter compilation lacks matching validated account and route evidence.");
        var rent = Convert.FromBase64String(state.Guard.RentBase64);
        if (tokenAccountRent != WttSolanaCostGuard.MinimumBalance(rent, 165))
            throw new InvalidDataException("Jupiter account rent differs from the guarded execution policy.");
        var setup = JupiterSetupInstructions.ValidateSetup(build.Setup, owner.Key, inputMint.Key, outputMint.Key);
        _ = JupiterSetupInstructions.ValidateCompute(build.Compute, computeUnits, computePrice);
        var fee = UInt64Text.Parse(terms.PlatformFee.Amount);
        var input = UInt64Text.Parse(terms.SwapInput);
        var gross = UInt64Text.Parse(terms.GrossInput);
        if (input == 0 || (UInt128)input + fee != gross || build.InputAmount != input ||
            build.MinimumOutput < UInt64Text.Parse(terms.MinimumOutput))
            throw new InvalidDataException("Jupiter compilation changed the exact input or consented minimum.");
        JupiterNativeAccount? native = null;
        if (terms.InputAsset.Native || terms.OutputAsset.Native)
        {
            if ((terms.InputAsset.Native ? inputMint : outputMint).Key != JupiterNativeAccount.Mint.Key)
                throw new InvalidDataException("Native SOL requires the canonical wrapped mint.");
            native = JupiterNativeAccount.Create(owner.Key, preparationKey, tokenAccountRent, terms.InputAsset.Native ? input : 0);
        }
        var instructions = new List<TransactionInstruction>
        {
            SolanaComputeBudget.Limit(computeUnits), SolanaComputeBudget.Price(computePrice),
            JupiterCostGuard.Build(new PublicKey(state.Guard.Program), rent,
                Convert.FromHexString(state.JupiterHash), Convert.FromHexString(state.WhirlpoolHash))
        };
        instructions.Add(MemoProgram.NewMemoV2(CanonicalJson.Hash(new
        {
            Domain = "azoa:jupiter-stage/1", Identity = effectIdentity ?? preparationKey
        }), owner));
        foreach (var instruction in build.Setup)
        {
            var address = instruction.Accounts[1].Address;
            if (native is not null && address == (terms.InputAsset.Native ? source : destination)) continue;
            if (!setup.Contains(address)) throw new InvalidDataException("Jupiter setup is outside its reviewed account set.");
            if (!state.MissingOwnerAccounts.Contains(address)) continue;
            instructions.Add(JupiterCompiledMessage.ToInstruction(instruction));
        }
        foreach (var missing in state.MissingOwnerAccounts)
        {
            if (setup.Contains(missing) || native is not null && missing == (terms.InputAsset.Native ? source : destination)) continue;
            var mint = missing == source ? inputMint : missing == destination ? outputMint
                : throw new InvalidDataException("Jupiter prerequisite is not an exact owner token account.");
            instructions.Add(JupiterCompiledMessage.ToInstruction(JupiterSetupInstructions.CreateOwnerAta(owner.Key, mint.Key)));
        }
        if (native is not null) instructions.AddRange(native.Before(owner.Key, preparationKey));
        if (fee > 0)
        {
            var treasury = new PublicKey(terms.PlatformFee.TreasuryAddress);
            if (treasury.Key == owner.Key) throw new InvalidDataException("A nonzero platform fee requires a distinct treasury.");
            instructions.Add(terms.InputAsset.Native
                ? SystemProgram.Transfer(owner, treasury, fee)
                : TokenProgram.TransferChecked(new PublicKey(source),
                    AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(treasury, inputMint), fee,
                    terms.InputAsset.Decimals, owner, inputMint));
        }
        var swap = build.Swap;
        if (native is not null)
        {
            var replaced = terms.InputAsset.Native ? source : destination;
            swap = swap with { Accounts = swap.Accounts.Select(account => account.Address == replaced
                ? account with { Address = native.Address } : account).ToArray() };
        }
        instructions.Add(JupiterCompiledMessage.ToInstruction(swap));
        if (native is not null) instructions.Add(native.After(owner.Key, preparationKey));
        var expected = instructions.Select(JupiterCompiledMessage.FromInstruction).ToArray();
        var legacy = CompileDurable(recovery, owner, instructions);
        var message = SolanaMessageEnvelope.CompileVersionZero(legacy, state.LookupTables, out var usedTables);
        JupiterCompiledMessage.Validate(message, usedTables, owner.Key, expected);
        return new(message, usedTables, expected, native);
    }
}
