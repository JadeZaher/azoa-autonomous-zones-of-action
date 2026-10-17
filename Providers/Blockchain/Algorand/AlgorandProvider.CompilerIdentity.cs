using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private async Task<string> ReadAlgodCompilerIdentityAsync(CancellationToken ct)
    {
        using var response = await AlgodHttpClient.GetAsync("/versions", ct);
        response.EnsureSuccessStatusCode();
        using var version = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        using var parametersResponse = await AlgodHttpClient.GetAsync("/v2/transactions/params", ct);
        parametersResponse.EnsureSuccessStatusCode();
        using var parameters = await JsonDocument.ParseAsync(await parametersResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = version.RootElement;
        var current = parameters.RootElement;
        if (!root.TryGetProperty("build", out var build) || build.ValueKind != JsonValueKind.Object
            || !build.TryGetProperty("commit_hash", out var commit) || commit.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(commit.GetString())
            || !root.TryGetProperty("genesis_hash_b64", out var genesis) || genesis.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("genesis_id", out var genesisId) || genesisId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(genesisId.GetString())
            || !current.TryGetProperty("genesis-hash", out var currentGenesis) || currentGenesis.ValueKind != JsonValueKind.String || currentGenesis.GetString() != genesis.GetString()
            || !current.TryGetProperty("genesis-id", out var currentGenesisId) || currentGenesisId.ValueKind != JsonValueKind.String || currentGenesisId.GetString() != genesisId.GetString()
            || !current.TryGetProperty("consensus-version", out var consensus) || consensus.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(consensus.GetString()))
            throw new InvalidOperationException("Configured Algod did not provide consistent compiler and genesis evidence.");
        var genesisBytes = new byte[32];
        if (!Convert.TryFromBase64String(genesis.GetString()!, genesisBytes, out var length) || length != 32
            || Convert.ToBase64String(genesisBytes) != genesis.GetString())
            throw new InvalidOperationException("Configured Algod genesis identity is not a canonical 32-byte hash.");
        foreach (var name in new[] { "major", "minor", "build_number" })
            if (!build.TryGetProperty(name, out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var value) || value < 0)
                throw new InvalidOperationException("Configured Algod build identity is incomplete.");
        return CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            scheme = "configured-algod-reported/1", build, genesis = genesis.GetString(),
            genesisId = genesisId.GetString(), consensus = consensus.GetString(),
        }));
    }
}
