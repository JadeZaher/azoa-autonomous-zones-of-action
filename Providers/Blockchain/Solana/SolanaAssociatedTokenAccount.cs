using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Associated-token-account instruction shapes shared by native, WTT and DEX builders.</summary>
internal static class SolanaAssociatedTokenAccount
{
    /// <summary>The ATA program's <c>CreateIdempotent</c> (opcode 1) over Solnet's canonical six-account create.</summary>
    public static TransactionInstruction CreateIdempotent(PublicKey payer, PublicKey owner, PublicKey mint)
    {
        var create = AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(payer, owner, mint);
        return new TransactionInstruction { ProgramId = create.ProgramId, Keys = create.Keys, Data = [1] };
    }
}
