using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider : IWttGuardianStateSource
{
    private sealed record WttAlgoDeployment(ulong Core, ulong Token, string Genesis,
        string CoreApproval, string CoreClear, string TokenApproval, string TokenClear);
    private sealed record WttAlgoSnapshot(WttAlgoDeployment Deployment, WttAlgorandApplication Core,
        WttAlgorandApplication Token, ulong Round, uint Timestamp, string BlockHash,
        uint CurrentGuardianSet, WttAlgorandStorage.Guardians? Guardians);

    /// <inheritdoc/>
    public async Task<AZOAResult<WttGuardianEvidence>> ReadWttGuardianEvidenceAsync(uint guardianSetIndex,
        ushort emitterChain, string emitterAddress, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await ReadWttAlgoSnapshotAsync(guardianSetIndex, emitterChain, emitterAddress, ct);
            var deployment = snapshot.Deployment;
            return AZOAResult<WttGuardianEvidence>.Success(new("algorand:" + deployment.Genesis,
                deployment.Core.ToString(CultureInfo.InvariantCulture), deployment.Token.ToString(CultureInfo.InvariantCulture),
                guardianSetIndex, snapshot.CurrentGuardianSet, snapshot.Guardians!.Addresses, (uint)snapshot.Guardians.ExpiresAt,
                snapshot.Timestamp, snapshot.BlockHash, DateTimeOffset.UtcNow, emitterChain, emitterAddress));
        }
        catch (InvalidDataException exception) { return AZOAResult<WttGuardianEvidence>.Failure(exception.Message); }
        catch (JsonException) { return AZOAResult<WttGuardianEvidence>.Failure("Algorand WTT returned malformed state JSON."); }
    }

    private async Task<WttAlgoSnapshot> ReadWttAlgoSnapshotAsync(uint? guardianSetIndex, ushort emitterChain,
        string? emitterAddress, CancellationToken ct)
    {
        _ = AlgodHttpClient;
        var deployment = WttAlgorandDeployment();
        if (guardianSetIndex.HasValue && (emitterChain == 0 || emitterAddress is not { Length: 64 } || !emitterAddress.All(char.IsAsciiHexDigitLower)))
            throw new InvalidDataException("A canonical foreign emitter is required.");
        var before = await ReadWttAlgorandJsonAsync("/v2/status", ct);
        var round = WttAlgoUnsigned(before, "last-round");
        if (round == 0) throw new InvalidDataException("A finalized Algorand round is required.");
        var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
        if (WttAlgoText(parameters, "genesis-hash") != deployment.Genesis)
            throw new InvalidDataException("Algorand WTT genesis differs from its configured deployment.");
        var core = WttAlgorandApplication.Read(await ReadWttAlgorandJsonAsync($"/v2/applications/{deployment.Core}", ct),
            deployment.Core, deployment.CoreApproval, deployment.CoreClear);
        var token = WttAlgorandApplication.Read(await ReadWttAlgorandJsonAsync($"/v2/applications/{deployment.Token}", ct),
            deployment.Token, deployment.TokenApproval, deployment.TokenClear);
        if (core is null || token is null || token.Get("coreid"u8)?.Integer != deployment.Core
            || token.Get("coreAddr"u8)?.Bytes is not { Length: 32 } coreAddress
            || !coreAddress.AsSpan().SequenceEqual(global::Algorand.Address.ForApplication(deployment.Core).Bytes)
            || core.Get("currentGuardianSetIndex"u8)?.Integer is not { } current || current > uint.MaxValue)
            throw new InvalidDataException("WTT application bytecode or core binding is not authoritative.");
        WttAlgorandStorage.Guardians? guardians = null;
        if (guardianSetIndex.HasValue)
        {
            if (WttAlgoEmitter(token, emitterChain) != emitterAddress)
                throw new InvalidDataException("Foreign emitter is not registered on the Algorand token bridge.");
            var guardianAddress = WttAlgorandStorage.Address(deployment.Core, guardianSetIndex.Value, "guardian"u8);
            var guardianAccount = await ReadWttAlgorandJsonAsync("/v2/accounts/" + guardianAddress, ct);
            var blob = WttAlgorandStorage.Read(guardianAccount, deployment.Core, guardianAddress, round);
            guardians = blob is null ? null : WttAlgorandStorage.ReadGuardians(blob);
            if (guardians is null || guardians.ExpiresAt > uint.MaxValue)
                throw new InvalidDataException("Authoritative Algorand guardian storage is unavailable or outside VAA timestamp range.");
        }
        var blockResponse = await ReadWttAlgorandJsonAsync($"/v2/blocks/{round}?format=json", ct);
        if (!blockResponse.TryGetProperty("block", out var block) || block.ValueKind != JsonValueKind.Object
            || WttAlgoUnsigned(block, "rnd") != round || WttAlgoText(block, "gh") != deployment.Genesis)
            throw new InvalidDataException("Algorand finalized block differs from the observed round and genesis.");
        var timestamp = WttAlgoUnsigned(block, "ts");
        if (timestamp > uint.MaxValue) throw new InvalidDataException("Algorand block timestamp exceeds the VAA timestamp range.");
        var hash = WttAlgoText(await ReadWttAlgorandJsonAsync($"/v2/blocks/{round}/hash", ct), "blockHash");
        if (hash.Length != 52 || hash.Any(value => value is not (>= 'A' and <= 'Z') and not (>= '2' and <= '7')))
            throw new InvalidDataException("Algorand block identity is malformed.");
        await RequireWttAlgoRoundAsync(round, ct);
        return new(deployment, core, token, round, (uint)timestamp, hash, (uint)current, guardians);
    }

    private async Task RequireWttAlgoRoundAsync(ulong round, CancellationToken ct)
    {
        var after = await ReadWttAlgorandJsonAsync("/v2/status", ct);
        if (WttAlgoUnsigned(after, "last-round") != round)
            throw new InvalidDataException("Algorand advanced during the WTT snapshot; repeat this read before verification.");
    }

    private static string WttAlgoEmitter(WttAlgorandApplication token, ushort chain)
    {
        var key = new byte[7]; "Chain"u8.CopyTo(key);
        BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(5), chain);
        if (chain == 0 || token.Get(key)?.Bytes is not { Length: 32 } registered || registered.All(value => value == 0))
            throw new InvalidDataException("Foreign emitter is not registered on the Algorand token bridge.");
        return Convert.ToHexStringLower(registered);
    }

    private WttAlgoDeployment WttAlgorandDeployment()
    {
        var prefix = $"AZOA:Algorand:Wtt:Networks:{ActiveNetwork}:";
        var genesis = _config[prefix + "GenesisHash"] ?? string.Empty;
        if (!bool.TryParse(_config[prefix + "Enabled"], out var enabled) || !enabled
            || !UInt64Text.TryParse(_config[prefix + "CoreBridge"], out var core) || core == 0
            || !UInt64Text.TryParse(_config[prefix + "TokenBridge"], out var token) || token == 0 || core == token)
            throw new InvalidDataException("An enabled pinned Algorand WTT deployment is required.");
        try
        {
            if (Convert.FromBase64String(genesis).Length != 32 || Convert.ToBase64String(Convert.FromBase64String(genesis)) != genesis)
                throw new InvalidDataException("Algorand WTT genesis must be a canonical digest.");
        }
        catch (FormatException) { throw new InvalidDataException("Algorand WTT genesis must be a canonical digest."); }
        var hashes = new[] { "CoreApprovalSha256", "CoreClearSha256", "TokenBridgeApprovalSha256", "TokenBridgeClearSha256" }
            .Select(name => _config[prefix + name] ?? string.Empty).ToArray();
        if (hashes.Any(hash => hash.Length != 64 || !hash.All(char.IsAsciiHexDigitLower)))
            throw new InvalidDataException("Reviewed Algorand WTT approval and clear program hashes are required.");
        return new(core, token, genesis, hashes[0], hashes[1], hashes[2], hashes[3]);
    }

    private async Task<JsonElement> ReadWttAlgorandJsonAsync(string path, CancellationToken ct, bool allowMissing = false)
    {
        using var response = await AlgodHttpClient.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (allowMissing) return default;
            throw new InvalidDataException("Required Algorand WTT state is absent.");
        }
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Algorand WTT returned a non-object response.");
        _ = CanonicalJson.Canonicalize(document.RootElement);
        return document.RootElement.Clone();
    }

    private static ulong WttAlgoUnsigned(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var number) ? number
            : throw new InvalidDataException("Algorand WTT unsigned field is missing or malformed: " + key);

    private static string WttAlgoText(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString()!
            : throw new InvalidDataException("Algorand WTT text field is missing or malformed: " + key);
}
