using System.Buffers.Binary;
using System.Text.Json;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

internal sealed record JupiterRpcSnapshot(ulong Slot, ulong ChainTimestamp,
    IReadOnlyDictionary<string, JsonElement> Accounts)
{
    internal static JupiterRpcSnapshot Read(JsonElement response, IReadOnlyList<string> addresses, ulong minimumSlot,
        string clockAddress)
    {
        if (addresses.Count is < 1 or > 100 || addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Count ||
            addresses.Any(address => new PublicKey(address).Key != address) || response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("context", out var context) || context.ValueKind != JsonValueKind.Object ||
            !context.TryGetProperty("slot", out var slotJson) || slotJson.ValueKind != JsonValueKind.Number ||
            !slotJson.TryGetUInt64(out var slot) || slot < minimumSlot ||
            !response.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() != addresses.Count)
            throw new InvalidDataException("Jupiter route requires a complete finalized account snapshot at its minimum context.");
        var accounts = addresses.Select((address, index) => (address, value: values[index].Clone()))
            .ToDictionary(pair => pair.address, pair => pair.value, StringComparer.Ordinal);
        if (clockAddress != SysVars.ClockKey.Key || !accounts.TryGetValue(clockAddress, out var clockAccount))
            throw new InvalidDataException("Jupiter route snapshot omits the canonical Clock.");
        var clock = SolanaAccountData.ReadOwned(clockAccount, "Sysvar1111111111111111111111111111111111111", false);
        if (clock.Length != 40 || BinaryPrimitives.ReadUInt64LittleEndian(clock) != slot)
            throw new InvalidDataException("Jupiter route Clock differs from its finalized snapshot.");
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(clock.AsSpan(32));
        if (timestamp < 0) throw new InvalidDataException("Jupiter route Clock timestamp is negative.");
        return new(slot, (ulong)timestamp, accounts);
    }

    internal byte[] Owned(string address, string owner, bool executable = false)
        => Accounts.TryGetValue(address, out var account) ? SolanaAccountData.ReadOwned(account, owner, executable)
            : throw new InvalidDataException("Jupiter route account evidence is incomplete.");

    internal void RequireProgram(PublicKey program, string codeHash)
    {
        var data = SolanaNativeLoader.ProgramData(program);
        if (!Accounts.TryGetValue(program.Key, out var executable) || !Accounts.TryGetValue(data.Key, out var code))
            throw new InvalidDataException("Jupiter route program-data evidence is incomplete.");
        SolanaProgramDataEvidence.Require(executable, code, data, codeHash);
    }

    internal WhirlpoolOracleState? ReadOracle(WhirlpoolPoolState pool)
    {
        var address = WhirlpoolAccountState.OracleAddress(pool.Address);
        if (!Accounts.TryGetValue(address, out var account)) throw new InvalidDataException("Jupiter route omitted Oracle evidence.");
        var uninitialized = account.ValueKind == JsonValueKind.Null;
        if (!uninitialized && account.ValueKind == JsonValueKind.Object && account.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.String && owner.GetString() == SystemProgram.ProgramIdKey.Key)
            uninitialized = SolanaAccountData.ReadOwned(account, SystemProgram.ProgramIdKey.Key, false).Length == 0;
        if (uninitialized)
        {
            if (pool.FeeTierIndex != pool.TickSpacing)
                throw new InvalidDataException("Adaptive Whirlpool requires its initialized Oracle.");
            return null;
        }
        return WhirlpoolOracleState.Read(address, SolanaAccountData.ReadOwned(account, WhirlpoolAccountState.Program.Key, false), pool, ChainTimestamp);
    }
}
