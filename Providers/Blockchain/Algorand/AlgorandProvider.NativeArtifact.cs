using System.Text.Json;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : INativeOperationModule
{
    private static readonly JsonSerializerOptions AvmJson = new(JsonSerializerDefaults.Web);
    private sealed record AvmPreparedPlan(string UnsignedBase64, string Genesis, string Rent, string IntentHash);

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeArtifact>> ValidateArtifactAsync(NativeArtifact artifact, CancellationToken ct = default)
    {
        if (artifact.Format != "avm-application/1" || !TryAvmArtifact(artifact, false, out var application))
            return Error<NativeArtifact>("A bounded typed AVM artifact with approval and clear programs is required.");
        var compilesSource = application.Approval.Kind == "Teal" || application.ClearState.Kind == "Teal";
        var compilerEvidence = compilesSource ? await ReadAlgodCompilerIdentityAsync(ct) : null;
        var approval = await CompileAvmProgramAsync(application.Approval, ct);
        var clear = await CompileAvmProgramAsync(application.ClearState, ct);
        if (approval is null || clear is null || approval.Length + clear.Length > 2048 * (1 + application.ExtraPages))
            return Error<NativeArtifact>("Compiled AVM programs exceed their declared page allocation.");
        if (compilesSource && compilerEvidence != await ReadAlgodCompilerIdentityAsync(ct))
            return Error<NativeArtifact>("Algod build or protocol changed during compilation; request a new artifact.");
        var normalized = application with { Approval = new("Bytecode", Convert.ToBase64String(approval)),
            ClearState = new("Bytecode", Convert.ToBase64String(clear)) };
        var provenance = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            scheme = "avm-program-provenance/1", compilerEvidence,
            approval = application.Approval.Kind == "Teal" ? "server-compiled" : "uploaded-bytecode",
            clear = application.ClearState.Kind == "Teal" ? "server-compiled" : "uploaded-bytecode",
        }));
        return Ok(artifact with { Content = JsonSerializer.SerializeToElement(normalized, AvmJson), CompilerIdentity = provenance });
    }

    private async Task<byte[]?> CompileAvmProgramAsync(AvmProgramInput program, CancellationToken ct)
    {
        if (program.Kind == "Bytecode") return Convert.FromBase64String(program.Data);
        using var content = new StringContent(program.Data, System.Text.Encoding.UTF8, "text/plain");
        using var response = await AlgodHttpClient.PostAsync("/v2/teal/compile", content, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        using var compiled = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return compiled.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
            ? Convert.FromBase64String(result.GetString()!) : null;
    }

    private bool TryAvmIntent(NativeOperationIntent intent, out AvmNativeInput input, out AvmApplicationArtifact application)
    {
        input = null!; application = null!;
        if (intent.Version != 1 || intent.ChainType != ChainType || intent.Network != ActiveNetwork.ToString()
            || string.IsNullOrWhiteSpace(intent.OperationKey) || intent.Authorities.Count != 1) return false;
        try { input = intent.Input.Deserialize<AvmNativeInput>(AvmJson)!; }
        catch (JsonException) { return false; }
        if (input is null || !ValidateAddressFormat(input.Sender) || input.Operation is not ("Create" or "Call")
            || !TryAvmArtifact(input.Artifact, true, out application)) return false;
        var create = input.Operation == "Create";
        var authority = intent.Authorities[0];
        return intent.Kind == (create ? "Deploy" : "Call") && authority.Address == input.Sender
            && !authority.SigningContext.IsPlatform && authority.SigningContext.IsResolvableUserContext
            && authority.SigningContext.Scope == (create ? AzoaScopes.ContractDeploySign : AzoaScopes.ContractCallSign)
            && (create ? input.ApplicationId is null : UInt64Text.TryParse(input.ApplicationId, out var id) && id > 0);
    }

    private static bool TryAvmArtifact(NativeArtifact artifact, bool compiled, out AvmApplicationArtifact application)
    {
        application = null!;
        if (artifact.Format != "avm-application/1") return false;
        try
        {
            var parsed = artifact.Content.Deserialize<AvmApplicationArtifact>(AvmJson)!;
            if (parsed is null || parsed.Approval is null || parsed.ClearState is null
                || parsed.GlobalSchema is null || parsed.LocalSchema is null
                || parsed.ArgumentsBase64 is null || parsed.Accounts is null || parsed.Boxes is null
                || parsed.ForeignApplications is null || parsed.ForeignAssets is null
                || parsed.ExtraPages > 3 || parsed.GlobalSchema.Integers > 64 || parsed.GlobalSchema.Bytes > 64
                || parsed.GlobalSchema.Integers + parsed.GlobalSchema.Bytes > 64
                || parsed.LocalSchema.Integers > 16 || parsed.LocalSchema.Bytes > 16
                || parsed.LocalSchema.Integers + parsed.LocalSchema.Bytes > 16
                || parsed.ArgumentsBase64.Count > 16 || parsed.Accounts.Count > 4
                || parsed.ForeignApplications.Count + parsed.ForeignAssets.Count + parsed.Accounts.Count + parsed.Boxes.Count > 8
                || parsed.ForeignApplications.Any(value => !UInt64Text.TryParse(value, out var id) || id == 0)
                || parsed.ForeignAssets.Any(value => !UInt64Text.TryParse(value, out var id) || id == 0)
                || parsed.ArgumentsBase64.Sum(value => Convert.FromBase64String(value).Length) > 2048
                || parsed.Boxes.Any(box => box is null || Convert.FromBase64String(box.NameBase64).Length > 64 || box.AppIndex > (ulong)parsed.ForeignApplications.Count)) return false;
            foreach (var program in new[] { parsed.Approval, parsed.ClearState })
            {
                if (program.Kind == "Bytecode")
                {
                    var bytes = Convert.FromBase64String(program.Data);
                    if (bytes.Length is 0 or > 8192) return false;
                }
                else if (compiled || program.Kind != "Teal" || string.IsNullOrWhiteSpace(program.Data) || program.Data.Length > 131072) return false;
            }
            if (parsed.Approval.Kind == "Bytecode" && parsed.ClearState.Kind == "Bytecode"
                && Convert.FromBase64String(parsed.Approval.Data).Length + Convert.FromBase64String(parsed.ClearState.Data).Length
                    > 2048 * (1 + parsed.ExtraPages)) return false;
            foreach (var account in parsed.Accounts) _ = new Address(account);
            application = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static ApplicationCallTransaction BuildAvmCall(AvmNativeInput input, AvmApplicationArtifact artifact)
    {
        ApplicationCallTransaction transaction = input.Operation == "Create"
            ? new ApplicationCreateTransaction
            {
                ApprovalProgram = new TEALProgram(Convert.FromBase64String(artifact.Approval.Data)),
                ClearStateProgram = new TEALProgram(Convert.FromBase64String(artifact.ClearState.Data)),
                ExtraProgramPages = artifact.ExtraPages,
                GlobalStateSchema = new StateSchema { NumUint = artifact.GlobalSchema.Integers, NumByteSlice = artifact.GlobalSchema.Bytes },
                LocalStateSchema = new StateSchema { NumUint = artifact.LocalSchema.Integers, NumByteSlice = artifact.LocalSchema.Bytes },
            }
            : new ApplicationNoopTransaction { ApplicationId = UInt64Text.Parse(input.ApplicationId!) };
        transaction.Sender = new Address(input.Sender);
        transaction.ApplicationArgs = artifact.ArgumentsBase64.Select(Convert.FromBase64String).ToArray();
        transaction.Accounts = artifact.Accounts.Select(address => new Address(address)).ToArray();
        transaction.ForeignApps = artifact.ForeignApplications.Select(UInt64Text.Parse).ToArray();
        transaction.ForeignAssets = artifact.ForeignAssets.Select(UInt64Text.Parse).ToArray();
        transaction.Boxes = artifact.Boxes.Select(box => new BoxRef { App = box.AppIndex, Name = Convert.FromBase64String(box.NameBase64) }).ToArray();
        return transaction;
    }

    private static ulong AvmMinimumBalance(AvmNativeInput input, AvmApplicationArtifact artifact)
        => input.Operation == "Create" ? checked(100_000UL * (1 + artifact.ExtraPages)
            + 28_500UL * artifact.GlobalSchema.Integers + 50_000UL * artifact.GlobalSchema.Bytes) : 0;

    private static ulong AvmFee(Transaction transaction, AlgodSuggestedParams parameters)
    {
        var size = (ulong)Encoder.EncodeToMsgPackOrdered(transaction).Length + 80UL;
        return checked(Math.Max((ulong)Math.Max(0, parameters.Fee) * size, (ulong)Math.Max(1000, parameters.MinFee)));
    }

    private async Task<bool> AvmApplicationMatchesAsync(string id, AvmApplicationArtifact artifact, CancellationToken ct)
    {
        using var response = await AlgodHttpClient.GetAsync($"/v2/applications/{id}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("approval-program", out var approval) && approval.GetString() == artifact.Approval.Data
            && parameters.TryGetProperty("clear-state-program", out var clear) && clear.GetString() == artifact.ClearState.Data;
    }
}
