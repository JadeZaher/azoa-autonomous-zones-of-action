using System.Text.Json;
using Solnet.Programs;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record JupiterClosePosition(int Instruction, int InnerInstruction);

/// <summary>Finds the first successful exact SPL close in finalized execution order.</summary>
internal static class JupiterCloseHistory
{
    internal static JupiterClosePosition? FirstClose(SolanaObservedTransaction.Accounts accounts, JsonElement meta, string account)
    {
        var error = meta.GetProperty("err");
        if (error.ValueKind != JsonValueKind.Null) return null;
        var groups = meta.GetProperty("innerInstructions");
        if (groups.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Complete inner-instruction history is required for rent attribution.");
        var indexed = new Dictionary<int, JsonElement>();
        foreach (var group in groups.EnumerateArray())
        {
            var index = group.GetProperty("index").GetInt32();
            if (index < 0 || index >= accounts.Message.Instructions.Count || !indexed.TryAdd(index, group.GetProperty("instructions")) ||
                indexed[index].ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Finalized inner-instruction positions are inconsistent.");
        }
        for (var index = 0; index < accounts.Message.Instructions.Count; index++)
        {
            var instruction = accounts.Message.Instructions[index];
            if (Closes(instruction.ProgramIndex, instruction.AccountIndices.Select(value => (int)value).ToArray(), instruction.Data))
                return new(index, -1);
            if (!indexed.TryGetValue(index, out var children)) continue;
            var ordinal = 0;
            foreach (var child in children.EnumerateArray())
            {
                var program = child.GetProperty("programIdIndex").GetInt32();
                var roles = child.GetProperty("accounts");
                if (roles.ValueKind != JsonValueKind.Array || child.GetProperty("data").GetString() is not { } encoded)
                    throw new InvalidDataException("Finalized inner instruction omitted compiled roles or bytes.");
                var data = Encoders.Base58.DecodeData(encoded);
                if (Encoders.Base58.EncodeData(data) != encoded)
                    throw new InvalidDataException("Finalized inner instruction data is not canonical.");
                if (Closes(program, roles.EnumerateArray().Select(value => value.GetInt32()).ToArray(), data))
                    return new(index, ordinal);
                ordinal++;
            }
        }
        return null;

        bool Closes(int program, IReadOnlyList<int> roles, byte[] data)
        {
            if (program < 0 || program >= accounts.Keys.Count || roles.Any(index => index < 0 || index >= accounts.Keys.Count))
                throw new InvalidDataException("Finalized instruction refers outside the resolved account list.");
            if (accounts.Keys[program].Key != TokenProgram.ProgramIdKey.Key || data.Length == 0 || data[0] != 9) return false;
            if (data.Length != 1 || roles.Count is < 3 or > 14)
                throw new InvalidDataException("Finalized SPL close is outside the canonical legacy grammar.");
            return accounts.Keys[roles[0]].Key == account;
        }
    }
}
