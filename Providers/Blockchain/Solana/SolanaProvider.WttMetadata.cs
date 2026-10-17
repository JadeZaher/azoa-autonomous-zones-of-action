using System.Buffers.Binary;
using System.Text.Json;
using Solnet.Programs;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private Task<SolanaMetadataCreationPolicy> ReadWttMetadataPolicyAsync(WttSolanaGuard guard, CancellationToken ct)
        => ReadSolanaMetadataPolicyAsync($"AZOA:Solana:Wtt:Networks:{ActiveNetwork}:",
            "AZOA:Solana:Wtt:ReviewedMetadataExecutables", guard.RentBase64, ct);

    private async Task<SolanaMetadataCreationPolicy> ReadSolanaMetadataPolicyAsync(string prefix, string catalogue,
        string rentBase64, CancellationToken ct)
    {
        var hash = _config[prefix + "MetadataProgramDataHash"];
        if (!IsNativeHash(hash)) throw new InvalidDataException("Metadata creation requires an explicit executable pin.");
        var policies = _config.GetSection(catalogue).GetChildren()
            .Where(entry => entry["CodeHash"] == hash)
            .Select(entry => new SolanaMetadataCreationPolicy(entry["CodeHash"]!, entry["SourceRevision"] ?? ""))
            .ToArray();
        if (policies.Length != 1)
            throw new InvalidDataException("Metadata executable is absent or ambiguous in the independently reviewed policy catalogue.");
        var policy = policies[0]; policy.RequireSupported();
        var program = SolanaNftMetadata.Program; var data = SolanaNativeLoader.ProgramData(program);
        var snapshot = await DurableRpcAsync("getMultipleAccounts", [new[] { program.Key, data.Key, SysVars.RentKey.Key, SysVars.ClockKey.Key },
            new { encoding = "base64", commitment = "finalized" }], ct);
        if (snapshot.ValueKind != JsonValueKind.Object || !snapshot.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetUInt64(out var slot) || !snapshot.TryGetProperty("value", out var accounts)
            || accounts.ValueKind != JsonValueKind.Array || accounts.GetArrayLength() != 4)
            throw new InvalidDataException("Metadata executable snapshot is malformed.");
        SolanaProgramDataEvidence.Require(accounts[0], accounts[1], data, hash!);
        var programData = SolanaAccountData.ReadOwned(accounts[1], SolanaNativeLoader.Program.Key, false);
        var rent = SolanaAccountData.ReadOwned(accounts[2], WttSysvarOwner, false);
        if (BinaryPrimitives.ReadUInt64LittleEndian(programData.AsSpan(4)) >= slot || programData[12] > 1
            || Convert.ToBase64String(rent) != rentBase64
            || !WttSolanaStateCodec.Clock(SolanaAccountData.ReadOwned(accounts[3], WttSysvarOwner, false), slot, out _))
            throw new InvalidDataException("Metadata deployment slot or frozen Rent snapshot differs from the selected policy.");
        _ = policy.Costs(rent);
        return policy;
    }
}
