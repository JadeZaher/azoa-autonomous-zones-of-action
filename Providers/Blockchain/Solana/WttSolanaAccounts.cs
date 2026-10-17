using System.Buffers.Binary;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Pinned Wormhole token-bridge PDA derivation and exact instruction account roles.</summary>
internal static class WttSolanaAccounts
{
    public static PublicKey Derive(PublicKey program, params byte[][] seeds)
        => PublicKey.TryFindProgramAddress(seeds, program, out var address, out _) ? address
            : throw new InvalidOperationException("WTT PDA derivation failed.");

    public static PublicKey WrappedMint(PublicKey tokenBridge, ushort originChain, byte[] originAddress)
    {
        if (originChain is 0 or 1 || originAddress.Length != 32) throw new ArgumentException("Wrapped mint requires a foreign token identity.");
        return Derive(tokenBridge, "wrapped"u8.ToArray(), ChainSeed(originChain), originAddress);
    }

    public static PublicKey Claim(PublicKey tokenBridge, ushort emitterChain, byte[] emitter, ulong sequence)
    {
        RequireEmitter(emitterChain, emitter);
        var sequenceSeed = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(sequenceSeed, sequence);
        return Derive(tokenBridge, emitter, ChainSeed(emitterChain), sequenceSeed);
    }

    public static TransactionInstruction Transfer(PublicKey tokenBridge, PublicKey core, PublicKey payer,
        PublicKey message, PublicKey from, PublicKey mint, ushort originChain, byte[] originAddress,
        uint nonce, ulong amount, ulong fee, byte[] recipient, ushort targetChain, PublicKey? sourceOwner = null)
    {
        if (message.Key == payer.Key) throw new ArgumentException("WTT message account must be distinct from the payer.");
        var wrapped = originChain != 1;
        if (originChain == 0 || originAddress.Length != 32
            || (wrapped ? WrappedMint(tokenBridge, originChain, originAddress).Key != mint.Key : !originAddress.AsSpan().SequenceEqual(mint.KeyBytes)))
            throw new ArgumentException("WTT source mint differs from its original token identity.");
        var keys = new List<AccountMeta>
        {
            AccountMeta.Writable(payer, true), AccountMeta.ReadOnly(Derive(tokenBridge, "config"u8.ToArray()), false),
            AccountMeta.Writable(from, false),
        };
        if (wrapped)
        {
            keys.Add(AccountMeta.ReadOnly(sourceOwner ?? payer, true));
            keys.Add(AccountMeta.Writable(mint, false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "meta"u8.ToArray(), mint.KeyBytes), false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "authority_signer"u8.ToArray()), false));
        }
        else
        {
            keys.Add(AccountMeta.Writable(mint, false));
            keys.Add(AccountMeta.Writable(Derive(tokenBridge, mint.KeyBytes), false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "authority_signer"u8.ToArray()), false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "custody_signer"u8.ToArray()), false));
        }
        PublicationAccounts(keys, tokenBridge, core, message);
        keys.Add(AccountMeta.ReadOnly(SysVars.RentKey, false));
        keys.Add(AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false));
        keys.Add(AccountMeta.ReadOnly(core, false));
        keys.Add(AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false));
        return new() { ProgramId = tokenBridge.KeyBytes, Keys = keys,
            Data = WttSolanaInstructionData.Transfer(wrapped, nonce, amount, fee, recipient, targetChain) };
    }

    public static TransactionInstruction Attest(PublicKey tokenBridge, PublicKey core, PublicKey payer,
        PublicKey message, PublicKey mint, uint nonce)
    {
        if (message.Key == payer.Key) throw new ArgumentException("WTT message account must be distinct from the payer.");
        var keys = new List<AccountMeta>
        {
            AccountMeta.Writable(payer, true), AccountMeta.Writable(Derive(tokenBridge, "config"u8.ToArray()), false),
            AccountMeta.ReadOnly(mint, false), AccountMeta.ReadOnly(Derive(tokenBridge, "meta"u8.ToArray(), mint.KeyBytes), false),
            AccountMeta.ReadOnly(SolanaNftMetadata.Metadata(mint), false),
        };
        PublicationAccounts(keys, tokenBridge, core, message);
        keys.Add(AccountMeta.Writable(SysVars.RentKey, false));
        keys.Add(AccountMeta.Writable(SystemProgram.ProgramIdKey, false));
        keys.Add(AccountMeta.ReadOnly(core, false));
        return new() { ProgramId = tokenBridge.KeyBytes, Keys = keys, Data = WttSolanaInstructionData.Attest(nonce) };
    }

    public static TransactionInstruction Complete(PublicKey tokenBridge, PublicKey core, PublicKey payer,
        PublicKey postedVaa, ushort emitterChain, byte[] emitter, ulong sequence, ushort originChain, byte[] originAddress,
        PublicKey recipientTokenAccount, PublicKey feeTokenAccount)
    {
        RequireEmitter(emitterChain, emitter);
        if (originChain == 0 || originAddress.Length != 32) throw new ArgumentException("WTT token identity is invalid.");
        var wrapped = originChain != 1;
        var mint = wrapped ? WrappedMint(tokenBridge, originChain, originAddress) : new PublicKey(originAddress);
        var keys = new List<AccountMeta>
        {
            AccountMeta.Writable(payer, true), AccountMeta.ReadOnly(Derive(tokenBridge, "config"u8.ToArray()), false),
            AccountMeta.ReadOnly(postedVaa, false), AccountMeta.Writable(Claim(tokenBridge, emitterChain, emitter, sequence), false),
            AccountMeta.ReadOnly(Derive(tokenBridge, ChainSeed(emitterChain), emitter), false),
            AccountMeta.Writable(recipientTokenAccount, false), AccountMeta.Writable(feeTokenAccount, false),
        };
        if (wrapped)
        {
            keys.Add(AccountMeta.Writable(mint, false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "meta"u8.ToArray(), mint.KeyBytes), false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "mint_signer"u8.ToArray()), false));
        }
        else
        {
            keys.Add(AccountMeta.Writable(Derive(tokenBridge, mint.KeyBytes), false));
            keys.Add(AccountMeta.ReadOnly(mint, false));
            keys.Add(AccountMeta.ReadOnly(Derive(tokenBridge, "custody_signer"u8.ToArray()), false));
        }
        keys.Add(AccountMeta.ReadOnly(SysVars.RentKey, false));
        keys.Add(AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false));
        keys.Add(AccountMeta.ReadOnly(core, false));
        keys.Add(AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false));
        return new() { ProgramId = tokenBridge.KeyBytes, Keys = keys, Data = WttSolanaInstructionData.Complete(wrapped) };
    }

    public static TransactionInstruction Register(PublicKey tokenBridge, PublicKey core, PublicKey payer,
        PublicKey postedVaa, ushort emitterChain, byte[] emitter, ulong sequence, ushort originChain, byte[] originAddress)
    {
        RequireEmitter(emitterChain, emitter);
        if (originChain != emitterChain) throw new ArgumentException("Wrapped registration requires an original-chain attestation.");
        var mint = WrappedMint(tokenBridge, originChain, originAddress);
        return new() { ProgramId = tokenBridge.KeyBytes, Data = WttSolanaInstructionData.Register(), Keys = new List<AccountMeta>
        {
            AccountMeta.Writable(payer, true), AccountMeta.ReadOnly(Derive(tokenBridge, "config"u8.ToArray()), false),
            AccountMeta.ReadOnly(Derive(tokenBridge, ChainSeed(emitterChain), emitter), false),
            AccountMeta.ReadOnly(postedVaa, false), AccountMeta.Writable(Claim(tokenBridge, emitterChain, emitter, sequence), false),
            AccountMeta.Writable(mint, false), AccountMeta.Writable(Derive(tokenBridge, "meta"u8.ToArray(), mint.KeyBytes), false),
            AccountMeta.Writable(SolanaNftMetadata.Metadata(mint), false), AccountMeta.ReadOnly(Derive(tokenBridge, "mint_signer"u8.ToArray()), false),
            AccountMeta.ReadOnly(SysVars.RentKey, false), AccountMeta.ReadOnly(SystemProgram.ProgramIdKey, false),
            AccountMeta.ReadOnly(core, false), AccountMeta.ReadOnly(TokenProgram.ProgramIdKey, false), AccountMeta.ReadOnly(SolanaNftMetadata.Program, false),
        } };
    }

    private static void PublicationAccounts(List<AccountMeta> keys, PublicKey tokenBridge, PublicKey core, PublicKey message)
    {
        var emitter = Derive(tokenBridge, "emitter"u8.ToArray());
        keys.Add(AccountMeta.Writable(Derive(core, "Bridge"u8.ToArray()), false));
        keys.Add(AccountMeta.Writable(message, true));
        keys.Add(AccountMeta.ReadOnly(emitter, false));
        keys.Add(AccountMeta.Writable(Derive(core, "Sequence"u8.ToArray(), emitter.KeyBytes), false));
        keys.Add(AccountMeta.Writable(Derive(core, "fee_collector"u8.ToArray()), false));
        keys.Add(AccountMeta.ReadOnly(SysVars.ClockKey, false));
    }

    private static byte[] ChainSeed(ushort chain)
    {
        var seed = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(seed, chain); return seed;
    }

    private static void RequireEmitter(ushort chain, byte[] emitter)
    {
        if (chain is 0 or 1 || emitter.Length != 32 || emitter.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("WTT target requires a foreign canonical emitter.");
    }
}
