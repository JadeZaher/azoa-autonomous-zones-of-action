using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record NativeAccount(byte[] Data, string Owner, bool Executable, ulong Lamports);

    /// <inheritdoc/>
    public Task<AZOAResult<NativeArtifact>> ValidateArtifactAsync(NativeArtifact artifact, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var valid = artifact.Format switch
            {
                "solana-elf/1" => artifact.Content.Deserialize<SolanaElfArtifact>(NativeSolanaJson) is { Loader: "loader-v3" } elf
                    && SolanaNativeLoader.IsElf(Convert.FromBase64String(elf.ElfBase64))
                    && (elf.SourceDigest is null || elf.SourceDigest == NativeHash(Convert.FromBase64String(elf.ElfBase64))),
                "solana-instruction/1" => artifact.Content.Deserialize<SolanaInstructionArtifact>(NativeSolanaJson) is { } instruction
                    && ValidateAddressFormat(instruction.ProgramId) && instruction.Accounts is { Count: <= 32 }
                    && instruction.Accounts.All(account => ValidateAddressFormat(account.Address))
                    && Convert.FromBase64String(instruction.DataBase64).Length <= 1024 && IsNativeHash(instruction.ExpectedProgramDataHash),
                "solana-account/1" => artifact.Content.Deserialize<SolanaAccountArtifact>(NativeSolanaJson) is { } account
                    && account.Operation is "CreateMetadata" or "UpdateMetadata" or "CreateAta" or "CloseTokenAccount"
                    && ValidateAddressFormat(account.Mint) && ValidateAddressFormat(account.Owner),
                _ => false,
            };
            return Task.FromResult(valid ? Ok(artifact) : Error<NativeArtifact>("Invalid bounded Solana artifact."));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        { return Task.FromResult(Error<NativeArtifact>("Malformed Solana artifact.")); }
    }

    private bool TrySolanaNativeIntent(NativeOperationIntent intent, out SolanaNativeInput input)
    {
        input = null!;
        if (intent.Version != 1 || intent.ChainType != ChainType || intent.Network != ActiveNetwork.ToString()
            || string.IsNullOrWhiteSpace(intent.OperationKey) || intent.Input.ValueKind != JsonValueKind.Object
            || intent.Authorities is not { Count: > 0 and <= 16 } || intent.Authorities.Any(authority => authority is null)) return false;
        try
        {
            if (intent.Kind == "Recovery")
            {
                var recovery = intent.Input.Deserialize<NativeRecoveryInput>(NativeSolanaJson);
                if (recovery is null || recovery.OriginalIntent is null || recovery.OriginalIntent.Kind == "Recovery"
                    || recovery.OriginalIntent.ChainType != intent.ChainType || recovery.OriginalIntent.Network != intent.Network
                    || recovery.OriginalIntent.ArtifactHash != intent.ArtifactHash
                    || recovery.OriginalIntent.Input.ValueKind != JsonValueKind.Object
                    || recovery.Mode is not ("AbortCleanup" or "ContinueCleanup" or "FinalizeAuthority")
                    || recovery.ResolvedHistory is null || recovery.ResolvedHistory.Count == 0
                    || recovery.ResolvedHistory.Any(item => item is null || item.Prepared is null || item.Observation is null)) return false;
                var original = recovery.OriginalIntent.Input.Deserialize<SolanaNativeInput>(NativeSolanaJson);
                if (original is null || original.Artifact is null || original.Artifact.Format != "solana-elf/1") return false;
                input = original with { Operation = "Recovery", Payer = recovery.PayerAddress,
                    RentRecipient = original.RentRecipient ?? original.Payer };
            }
            else input = intent.Input.Deserialize<SolanaNativeInput>(NativeSolanaJson)!;
        }
        catch (JsonException) { return false; }
        if (input is null || input.Artifact is null || !ValidateAddressFormat(input.Payer) || !ValidateAddressFormat(input.Authority)
            || input.RentRecipient is not null && !ValidateAddressFormat(input.RentRecipient)) return false;
        var scope = input.Artifact.Format switch
        {
            "solana-elf/1" when input.Operation is "Create" or "Upgrade" or "Recovery" => AzoaScopes.ContractDeploySign,
            "solana-instruction/1" when input.Operation == "Call" => AzoaScopes.ContractCallSign,
            "solana-account/1" when input.Operation == "Account" => AzoaScopes.SolanaAccountSign,
            _ => null,
        };
        return scope is not null && intent.Kind == (input.Operation == "Recovery" ? "Recovery" : input.Operation == "Account" ? "Account" : input.Operation == "Call" ? "Call" : "Deploy")
            && intent.Authorities.All(authority => ValidateAddressFormat(authority.Address) && !authority.SigningContext.IsPlatform
                && authority.SigningContext.IsResolvableUserContext && authority.SigningContext.Scope == scope)
            && intent.Authorities.Any(authority => authority.Address == input.Payer)
            && intent.Authorities.Any(authority => authority.Address == input.Authority)
            && intent.Authorities.GroupBy(authority => authority.Address, StringComparer.Ordinal)
                .All(group => group.Select(authority => authority.SigningContext).Distinct().Count() == 1);
    }

    private async Task<NativeSolanaQuote> PlanSolanaNativeAsync(NativeOperationIntent intent, SolanaNativeInput input, CancellationToken ct)
    {
        if (input.Operation == "Recovery") return await PlanSolanaRecoveryAsync(intent, input, ct);
        if ((await ValidateArtifactAsync(input.Artifact, ct)).IsError) throw new ArgumentException("Invalid Solana native artifact.");
        if (input.Operation == "Account") return await PlanSolanaAccountAsync(input, ct);
        if (input.Operation == "Call")
        {
            var instruction = input.Artifact.Content.Deserialize<SolanaInstructionArtifact>(NativeSolanaJson)!;
            await RequireProgramHashAsync(instruction.ProgramId, instruction.ExpectedProgramDataHash!, ct);
            if (instruction.Accounts.Where(account => account.Signer).Any(account => !intent.Authorities.Any(authority => authority.Address == account.Address)))
                throw new ArgumentException("Every native instruction signer requires its actual custody authority.");
            return new(instruction.ProgramId, [NativeStage("Call", 0, DecodeNativeInstruction(new(instruction.ProgramId, instruction.Accounts, instruction.DataBase64)))], instruction.ExpectedProgramDataHash);
        }
        var elf = input.Artifact.Content.Deserialize<SolanaElfArtifact>(NativeSolanaJson)!;
        var bytes = Convert.FromBase64String(elf.ElfBase64);
        var authority = new PublicKey(input.Authority); var payer = new PublicKey(input.Payer);
        var bufferSeed = SolanaNativeLoader.Seed(intent.OperationKey, "buffer");
        var buffer = SolanaNativeLoader.Seeded(authority, bufferSeed);
        if (await ReadNativeAccountAsync(buffer.Key, ct) is not null) throw new ArgumentException("Native buffer identity already exists; recover its original operation.");
        var programSeed = SolanaNativeLoader.Seed(intent.OperationKey, "program");
        var program = input.Operation == "Create" ? SolanaNativeLoader.Seeded(authority, programSeed) : new PublicKey(input.ProgramId!);
        var maximum = input.MaximumDataLength == 0 ? (uint)bytes.Length : input.MaximumDataLength;
        if (maximum < bytes.Length || maximum > 1_048_576) throw new ArgumentException("Invalid native program capacity.");
        if (input.Operation == "Create")
        {
            if (input.ProgramId is not null && input.ProgramId != program.Key || await ReadNativeAccountAsync(program.Key, ct) is not null)
                throw new ArgumentException("Native program identity already exists or differs from the seeded operation.");
        }
        else
        {
            if (!IsNativeHash(input.ExpectedProgramDataHash)) throw new ArgumentException("Upgrade requires the exact current program-data hash.");
            var data = await RequireProgramHashAsync(program.Key, input.ExpectedProgramDataHash!, ct);
            if (data.Data.Length < SolanaNativeLoader.ProgramDataHeaderSize + bytes.Length || data.Data[12] != 1
                || new PublicKey(data.Data.AsSpan(13, 32).ToArray()).Key != input.Authority)
                throw new ArgumentException("Upgrade capacity or current authority differs from the request.");
            maximum = checked((uint)(data.Data.Length - SolanaNativeLoader.ProgramDataHeaderSize));
        }
        var bufferRent = await NativeRentAsync(SolanaNativeLoader.BufferHeaderSize + bytes.Length, ct);
        var stages = new List<NativeSolanaStage>
        {
            NativeStage("CreateBuffer", bufferRent,
                SystemProgram.CreateAccountWithSeed(payer, buffer, authority, bufferSeed, bufferRent,
                    (ulong)(SolanaNativeLoader.BufferHeaderSize + bytes.Length), SolanaNativeLoader.Program),
                SolanaNativeLoader.InitializeBuffer(buffer, authority)),
        };
        for (var offset = 0; offset < bytes.Length; offset += SolanaNativeLoader.WriteChunkSize)
            stages.Add(NativeStage("Write:" + offset.ToString(CultureInfo.InvariantCulture), 0,
                SolanaNativeLoader.Write(buffer, authority, (uint)offset, bytes.AsSpan(offset, Math.Min(SolanaNativeLoader.WriteChunkSize, bytes.Length - offset)).ToArray())));
        if (input.Operation == "Create")
        {
            var programRent = await NativeRentAsync(SolanaNativeLoader.ProgramSize, ct);
            var dataRent = await NativeRentAsync(checked(SolanaNativeLoader.ProgramDataHeaderSize + (int)maximum), ct);
            stages.Add(NativeStage("Deploy", checked(programRent + dataRent),
                SystemProgram.CreateAccountWithSeed(payer, program, authority, programSeed, programRent, SolanaNativeLoader.ProgramSize, SolanaNativeLoader.Program),
                SolanaNativeLoader.Deploy(payer, program, buffer, authority, maximum)));
        }
        else stages.Add(NativeStage("Upgrade", 0, SolanaNativeLoader.Upgrade(payer, program, buffer, authority)));
        if (input.Immutable) stages.Add(NativeStage("RemoveAuthority", 0, SolanaNativeLoader.RemoveAuthority(program, authority)));
        var expectedProgram = new byte[checked((int)maximum)];
        bytes.CopyTo(expectedProgram, 0);
        var refundDestination = input.RentRecipient ?? input.Payer;
        return new(program.Key, stages, NativeHash(expectedProgram),
            new NativeOperationDisclosure(input.Operation == "Create" ? "DeployProgram" : "UpgradeProgram", input.Payer,
                CleanupRefundDestination: refundDestination),
            NativeStage("CloseBuffer", 0, SolanaNativeLoader.CloseBuffer(buffer, new PublicKey(refundDestination), authority)));
    }

    private static NativeSolanaStage NativeStage(string name, ulong rent, params TransactionInstruction[] instructions)
        => new(name, instructions.Select(EncodeNativeInstruction).ToArray(), "0", rent.ToString(CultureInfo.InvariantCulture));
    private async Task<ulong> NativeRentAsync(int bytes, CancellationToken ct)
        => (await DurableRpcAsync("getMinimumBalanceForRentExemption", [bytes, new { commitment = "finalized" }], ct)).GetUInt64();
    private async Task<NativeAccount?> ReadNativeAccountAsync(string address, CancellationToken ct)
    {
        var response = await DurableRpcAsync("getAccountInfo", [address, new { encoding = "base64", commitment = "finalized" }], ct);
        var account = response.GetProperty("value");
        return account.ValueKind == JsonValueKind.Null ? null : new(Convert.FromBase64String(account.GetProperty("data")[0].GetString()!),
            account.GetProperty("owner").GetString()!, account.GetProperty("executable").GetBoolean(), account.GetProperty("lamports").GetUInt64());
    }
    private async Task<NativeAccount> RequireProgramHashAsync(string address, string hash, CancellationToken ct)
    {
        var account = await ReadNativeAccountAsync(address, ct) ?? throw new ArgumentException("Native program is absent.");
        if (!account.Executable || account.Owner != SolanaNativeLoader.Program.Key || account.Data.Length != SolanaNativeLoader.ProgramSize
            || BinaryPrimitives.ReadUInt32LittleEndian(account.Data) != 2) throw new ArgumentException("Expected an executable loader-v3 program.");
        var dataAddress = new PublicKey(account.Data.AsSpan(4, 32).ToArray());
        if (dataAddress.Key != SolanaNativeLoader.ProgramData(new PublicKey(address)).Key) throw new ArgumentException("Program-data derivation differs.");
        var data = await ReadNativeAccountAsync(dataAddress.Key, ct) ?? throw new ArgumentException("Native program data is absent.");
        if (data.Owner != SolanaNativeLoader.Program.Key || data.Data.Length < SolanaNativeLoader.ProgramDataHeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Data) != 3 || NativeHash(data.Data.AsSpan(SolanaNativeLoader.ProgramDataHeaderSize).ToArray()) != hash)
            throw new ArgumentException("Native program code differs from the authorized hash.");
        return data;
    }
    private static string NativeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsNativeHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
