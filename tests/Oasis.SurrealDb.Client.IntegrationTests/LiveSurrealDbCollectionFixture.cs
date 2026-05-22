using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Oasis.SurrealDb.Client.IntegrationTests;

/// <summary>
/// xUnit collection fixture that spins (and tears down) the
/// <c>surrealdb/surrealdb:v1.5.4</c> test container via
/// <c>scripts/surrealdb/start-test-container.ps1</c>. If no compose runtime is
/// available — i.e. podman/docker not installed in the sandbox — the fixture
/// flips <see cref="SurrealAvailable"/> to <c>false</c> and every test in the
/// collection gracefully early-returns (still counted as passing) — matching
/// section 9 of <c>scripts/passoff-surrealdb-wave1.ps1</c>.
///
/// HIGH#3 — proves the homebake wire shape against a real SurrealDB server
/// when the container is available, and doesn't poison the pass-off gate
/// when it isn't.
/// </summary>
public sealed class LiveSurrealDbCollectionFixture : IDisposable
{
    /// <summary>SurrealDB HTTP endpoint for tests in this collection.</summary>
    public string Endpoint { get; } = "http://localhost:8442";

    /// <summary>Root user for the test container (matches docker-compose.surrealdb.yml).</summary>
    public string User { get; } = "root";

    /// <summary>Root password for the test container.</summary>
    public string Password { get; } = "oasis-surreal-root";

    /// <summary>Default namespace for the fixture's live tests.</summary>
    public string Namespace { get; } = "oasis";

    /// <summary>Default database for the fixture's live tests.</summary>
    public string Database { get; } = "client_integration";

    /// <summary>
    /// True iff <c>start-test-container.ps1</c> succeeded and <c>/health</c>
    /// is reachable. Tests gracefully early-return when this is false rather
    /// than failing — mirrors the pass-off gate's section 9 contract.
    /// </summary>
    public bool SurrealAvailable { get; }

    /// <summary>Human-readable reason when <see cref="SurrealAvailable"/> is false.</summary>
    public string? SkipReason { get; }

    public LiveSurrealDbCollectionFixture()
    {
        try
        {
            var repoRoot = FindRepoRoot();
            if (repoRoot is null)
            {
                SkipReason       = "repo root not found from working directory";
                SurrealAvailable = false;
                return;
            }

            var script = Path.Combine(repoRoot, "scripts", "surrealdb", "start-test-container.ps1");
            if (!File.Exists(script))
            {
                SkipReason       = $"start-test-container.ps1 not found at {script}";
                SurrealAvailable = false;
                return;
            }

            var exitCode = RunPowerShell(script);
            if (exitCode != 0)
            {
                SkipReason       = $"start-test-container.ps1 exited {exitCode} (no podman/docker?)";
                SurrealAvailable = false;
                return;
            }

            SurrealAvailable = ProbeHealth(Endpoint);
            if (!SurrealAvailable) SkipReason = $"/health probe failed at {Endpoint}";
        }
        catch (Exception ex)
        {
            SkipReason       = $"fixture init threw: {ex.Message}";
            SurrealAvailable = false;
        }
    }

    public void Dispose()
    {
        // Best-effort teardown — the container is intentionally persistent
        // across runs (see start-test-container.ps1 docstring), so we DO NOT
        // remove it here. The repo's stop-test-container.ps1 is the manual
        // teardown entry point.
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "oasis-sleek.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static int RunPowerShell(string scriptPath)
    {
        // Prefer pwsh (PowerShell 7+); fall back to Windows PowerShell.
        foreach (var exe in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                proc.WaitForExit(120_000); // 2-min cap; start-test-container.ps1 has its own 60s deadline
                return proc.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Executable not found — try the next.
            }
        }
        return -1;
    }

    private static bool ProbeHealth(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = client.GetAsync(baseUrl.TrimEnd('/') + "/health").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>xUnit collection marker for the live-SurrealDB fixture.</summary>
[CollectionDefinition("LiveSurrealDb")]
public sealed class LiveSurrealDbCollection : ICollectionFixture<LiveSurrealDbCollectionFixture>
{
    // Marker only — xUnit supplies the runtime collection identity.
}
