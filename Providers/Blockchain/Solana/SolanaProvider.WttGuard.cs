using System.Buffers.Binary;
using System.Text.Json;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record WttSolanaGuard(string Program, string CodeHash, string RentBase64);

    private Task<WttSolanaGuard> ReadWttSolanaGuardAsync(CancellationToken ct)
        => ReadSolanaCostGuardAsync($"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:", ct);

    private async Task<WttSolanaGuard> ReadSolanaCostGuardAsync(string prefix, CancellationToken ct)
    {
        if (!IsNativeHash(_config[prefix + "CostGuardProgramDataHash"]))
            throw new InvalidDataException("Solana execution requires a pinned immutable cost guard.");
        var program = new PublicKey(_config[prefix + "CostGuardProgram"] ?? "");
        var dataAddress = SolanaNativeLoader.ProgramData(program);
        var result = await DurableRpcAsync("getMultipleAccounts", [new[] { program.Key, dataAddress.Key, SysVars.RentKey.Key, SysVars.ClockKey.Key },
            new { encoding = "base64", commitment = "finalized" }], ct);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetUInt64(out var slot)
            || !result.TryGetProperty("value", out var accounts) || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != 4)
            throw new InvalidDataException("Solana cost guard snapshot is malformed.");
        var hash = _config[prefix + "CostGuardProgramDataHash"]!;
        SolanaProgramDataEvidence.Require(accounts[0], accounts[1], dataAddress, hash);
        var programData = SolanaAccountData.ReadOwned(accounts[1], SolanaNativeLoader.Program.Key, false);
        if (programData[12] != 0 || BinaryPrimitives.ReadUInt64LittleEndian(programData.AsSpan(4)) >= slot
            || !WttSolanaStateCodec.Clock(SolanaAccountData.ReadOwned(accounts[3], WttSysvarOwner, false), slot, out _))
            throw new InvalidDataException("Solana cost guard must have removed upgrade authority and a prior-slot deployment.");
        var rent = SolanaAccountData.ReadOwned(accounts[2], WttSysvarOwner, false);
        _ = WttSolanaCostGuard.MinimumBalance(rent, 0);
        return new(program.Key, hash, Convert.ToBase64String(rent));
    }
}
