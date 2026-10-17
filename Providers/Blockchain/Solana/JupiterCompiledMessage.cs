using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Checks complete compiled instructions and account privileges against the reviewed plan.</summary>
internal static class JupiterCompiledMessage
{
    internal static void Validate(byte[] messageBytes, IReadOnlyList<SolanaLookupTableSnapshot> tables,
        string payer, IReadOnlyList<JupiterInstruction> expected)
    {
        var message = SolanaMessageEnvelope.Read(messageBytes);
        var keys = SolanaMessageEnvelope.Resolve(message, tables);
        if (message.StaticKeys[0].Key != payer || message.RequiredSignatures != 1 ||
            message.Instructions.Count != expected.Count || expected.Count is < 1 or > 32)
            throw new InvalidDataException("Jupiter message changed payer, signers or instruction count.");
        var privileges = new Dictionary<string, (bool Signer, bool Writable)>(StringComparer.Ordinal)
        {
            [payer] = (true, true)
        };
        foreach (var instruction in expected)
        {
            Add(instruction.Program, false, false);
            foreach (var account in instruction.Accounts) Add(account.Address, account.Signer, account.Writable);
        }
        if (keys.Count != privileges.Count)
            throw new InvalidDataException("Jupiter message contains unused or missing account authority.");
        var resolved = new SolanaObservedTransaction.Accounts(message, keys);
        for (var index = 0; index < keys.Count; index++)
            if (!privileges.TryGetValue(keys[index].Key, out var role) || role.Signer != (index < message.RequiredSignatures) ||
                role.Writable != resolved.Writable(index))
                throw new InvalidDataException("Jupiter compiled privilege union differs from the exact plan.");
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = message.Instructions[index];
            var instruction = expected[index];
            if (keys[actual.ProgramIndex].Key != instruction.Program || !actual.Data.AsSpan().SequenceEqual(instruction.Data) ||
                !actual.AccountIndices.Select(account => keys[account].Key).SequenceEqual(instruction.Accounts.Select(account => account.Address)))
                throw new InvalidDataException("Jupiter compiled instruction order, data or account roles changed.");
        }
        _ = SolanaComputeBudget.SimulationWire(messageBytes);

        void Add(string address, bool signer, bool writable)
        {
            if (signer && address != payer) throw new InvalidDataException("Jupiter plan requests an unrelated signer.");
            var prior = privileges.GetValueOrDefault(address);
            privileges[address] = (prior.Signer || signer, prior.Writable || writable);
        }
    }

    internal static TransactionInstruction ToInstruction(JupiterInstruction instruction) => new()
    {
        ProgramId = new PublicKey(instruction.Program).KeyBytes,
        Data = instruction.Data.ToArray(),
        Keys = instruction.Accounts.Select(account => account.Writable
            ? AccountMeta.Writable(new PublicKey(account.Address), account.Signer)
            : AccountMeta.ReadOnly(new PublicKey(account.Address), account.Signer)).ToList()
    };

    internal static JupiterInstruction FromInstruction(TransactionInstruction instruction)
        => new(new PublicKey(instruction.ProgramId).Key, instruction.Keys.Select(account =>
            new JupiterInstructionAccount(account.PublicKey, account.IsSigner, account.IsWritable)).ToArray(), instruction.Data.ToArray());
}
