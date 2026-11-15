using System.Text.Json;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Gates;

/// <summary>Checks durability configuration without starting a host or database fixture.</summary>
[Trait("Category", "Chaos")]
public sealed class G1_DurabilityConfigurationTest
{
    /// <summary>Checks release storage and the preserved legacy dev durability posture.</summary>
    [Fact]
    public void G1_DurabilityAckGate_FailsClosed_IfSyncEventual()
    {
        var repoRoot = FindLocalRepoRoot();
        var releaseDirectory = Path.Combine(repoRoot, "deploy", "railway");
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(releaseDirectory, "surreal-contract.json")));
        var storage = contract.RootElement.GetProperty("storage");
        storage.GetProperty("engine").GetString().Should().Be("surrealkv");
        storage.GetProperty("sync").GetString().Should().Be("every");
        storage.GetProperty("uri").GetString().Should().Be("surrealkv:///data/db?sync=every");
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(releaseDirectory, "template.json")));
        var database = template.RootElement.GetProperty("services").EnumerateArray()
            .Single(service => service.GetProperty("name").GetString() == "surrealdb");
        database.GetProperty("variables").GetProperty("SURREAL_PATH").GetString()
            .Should().Be("<SUPPORTED_SURREAL_STORAGE_URI>");
        database.GetProperty("volume").GetProperty("mountPath").GetString().Should().Be("/data");

        var legacyDev = File.ReadAllText(Path.Combine(repoRoot, "docker-compose.dev.yml"));
        legacyDev.Should().Contain("rocksdb://", "the existing legacy dev volume must not switch engines implicitly");
        legacyDev.Should().Contain("SURREAL_SYNC_DATA: \"true\"", "the legacy RocksDB profile still requires synchronous commits");
    }

    // Locate checked-out configuration; see Gates/AGENTS.md.
    private static string FindLocalRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate repo root (directory containing AZOA.WebAPI.csproj). " +
            "Ensure the test assembly is built from within the azoa repository.");
    }
}
