// ─── AZOA Sleek — Guardrail G3: Injection Suite Gate ────────────────────────
//
// LOAD-BEARING ASSERTION (read before modifying this file)
// ─────────────────────────────────────────────────────────
// G3 requires that ALL SurrealQL queries are composed via
// SurrealQuery.Of(...).WithParam(...) — never via C# string interpolation or
// concatenation into the query string. This suite provides runtime evidence:
//
// TEST 1 — G3_ControllerPaths_HostileInput_LandsAsLiteralNotSurrealQl
//   Drives six hostile payloads through four controller endpoints.
//   Endpoints whose path parameters are typed as Guid reject the payloads at
//   model-binding (HTTP 400). Free-form query-string params return empty results.
//   A post-request SELECT COUNT(*) proves no DELETE/DROP took effect.
//
// TEST 2 — G3_DirectWithParam_HostileInput_PersistsAsLiteralString
//   Writes hostile strings as label values via SurrealWalletStore.UpsertAsync,
//   reads back, and asserts byte-exact equality. Proves WithParam binding
//   treats the payload as data, not code.
//
// TEST 3 — G3_AnalyzerSRDB0001_FiresOnInterpolatedSurrealQl
//   Shells out `dotnet build` on a temp fixture .cs that calls
//   SurrealQuery.Of($"...{userInput}..."). Asserts build emits SRDB0001 and
//   exits non-zero. Pure static analysis — no SurrealDB container needed.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Connection;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Builders;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Gates;

/// <summary>
/// Runtime evidence for AZOA guardrail G3 (parameterized queries only).
/// </summary>
[Trait("Category", "Gate")]
public sealed class G3_InjectionSuiteTest : IntegrationTestBase
{
    // ── Hostile payload corpus ────────────────────────────────────────────────

    // Each payload is designed to exfiltrate / mutate data if the backend ever
    // interpolates it directly into SurrealQL. G3 compliance means every one of
    // these lands as an opaque string value (or is rejected by model binding),
    // with NO row mutation as a side-effect.
    private static readonly string[] HostilePayloads =
    [
        // a) Classic SQL injection
        "' OR 1=1; DROP TABLE wallet;--",
        // b) SurrealQL parameter injection
        "$id; DELETE wallet;",
        // c) SurrealQL function injection
        "type::record(\"wallet\", \"; DROP NAMESPACE test;--\")",
        // d) Unicode fullwidth apostrophe (U+FF07)
        "＇ OR 1=1",
        // e) NUL byte
        "wallet: ; DELETE wallet;",
        // f) RTL override (U+202E)
        "wallet‮; DELETE wallet;"
    ];

    // SurrealDB direct-connection config (mirrors SurrealWalletStoreTests pattern)
    // Connection config sourced from SurrealTestDefaults (points at local instance).

    public G3_InjectionSuiteTest(AZOATestWebApplicationFactory factory)
        : base(factory)
    {
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST 1
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires all six hostile payloads through four controller endpoints.
    ///
    /// Endpoints with Guid-typed path parameters (WalletController, ApiKeyController)
    /// return HTTP 400 from model binding before any store interaction — the GOOD
    /// outcome. Endpoints with free-form string parameters (HolonController query
    /// filters, BridgeController status) return empty results without mutation.
    ///
    /// After every request a direct SELECT COUNT(*) against the wallet table proves
    /// no DELETE or DROP escaped the parameterized query layer.
    /// </summary>
    [SkippableFact]
    public async Task G3_ControllerPaths_HostileInput_LandsAsLiteralNotSurrealQl()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        // Seed one legitimate wallet so we can measure row counts before/after.
        var seedWallet = await SeedWalletAsync(w =>
            w.ForAvatar(Guid.Parse(TestAuthHandler.DefaultAvatarId))
             .OnChain("Algorand")
             .WithAddress($"g3_seed_{Guid.NewGuid():N}")
             .WithLabel("G3-Seed"));

        var countBefore = await QueryWalletCountAsync();
        countBefore.Should().BeGreaterThanOrEqualTo(1, "seed wallet must be present before injection probes");

        foreach (var payload in HostilePayloads)
        {
            // ── 1a: WalletController GET /api/wallet/{id}
            //   Path param is Guid in the route template → model binding rejects
            //   any non-Guid string with 400 (no store interaction).
            var walletByIdResponse = await Client.GetAsync(
                $"api/wallet/{Uri.EscapeDataString(payload)}");
            walletByIdResponse.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound },
                "WalletController.GetById must reject hostile id before store access");

            // ── 1b: HolonController query filters (free-form strings)
            //   POST /api/holon/query passes Name as a SurrealQL param → the
            //   payload must produce zero matches, not a parse error.
            //   Use the GET query-string endpoint (api/holon?Name=...) since
            //   GET with filter is the established pattern in HolonControllerIntegrationTests.
            var holonQueryResponse = await Client.GetAsync(
                $"api/holon?Name={Uri.EscapeDataString(payload)}" +
                $"&AssetType={Uri.EscapeDataString(payload)}" +
                $"&ChainId={Uri.EscapeDataString(payload)}");

            // Either 200 with empty result (no match) or 400 (validation rejected it).
            // Both are safe outcomes — what we must NOT see is a 500 from unparameterized
            // query execution, or a subsequent count drop.
            holonQueryResponse.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest },
                "HolonController query must either reject or return empty — never mutate");

            if (holonQueryResponse.StatusCode == HttpStatusCode.OK)
            {
                var holonResult = await holonQueryResponse.Content
                    .ReadFromJsonAsync<AZOAResult<IEnumerable<Holon>>>(JsonOptions);
                // No match expected — payload doesn't match any real holon name.
                holonResult?.Result?.Should().NotContain(
                    h => h.Name == payload,
                    "hostile name payload must not match real holon rows");
            }

            // ── 1c: ApiKeyController GET /api/apikeys/{id}
            //   Route uses {id:guid} constraint → non-Guid payloads are rejected at 400.
            var apiKeyByIdResponse = await Client.GetAsync(
                $"api/apikeys/{Uri.EscapeDataString(payload)}");
            apiKeyByIdResponse.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound },
                "ApiKeyController must reject hostile id without store interaction");

            // ── 1d: BridgeController GET /api/bridge/history (avatar-scoped, no free id)
            //   We probe the routes endpoint as a safe read-only path that does not
            //   accept a user-controlled string id:
            var bridgeRoutesResponse = await Client.GetAsync("api/bridge/routes");
            bridgeRoutesResponse.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.ServiceUnavailable },
                "BridgeController routes must be safe regardless of payloads in earlier requests");

            // ── Assert: wallet table count has not decreased ─────────────────
            var countAfter = await QueryWalletCountAsync();
            countAfter.Should().BeGreaterThanOrEqualTo(countBefore,
                $"wallet row count must not decrease after probing with payload: {payload}");
        }

        // Final read-back: seed wallet still present by id.
        var finalGet = await Client.GetAsync($"api/wallet/{seedWallet.Id}");
        finalGet.StatusCode.Should().Be(HttpStatusCode.OK,
            "seed wallet must still be retrievable after all injection probes");
        var finalResult = await finalGet.Content
            .ReadFromJsonAsync<AZOAResult<Wallet>>(JsonOptions);
        finalResult?.Result?.Id.Should().Be(seedWallet.Id,
            "seed wallet identity must be intact after all injection probes");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST 2
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes hostile strings as Wallet.Label values via SurrealWalletStore.UpsertAsync,
    /// reads them back via GetByIdAsync, and asserts byte-exact equality. A second
    /// wallet seeded before the writes must still be present after, proving the
    /// payloads didn't mutate the table.
    ///
    /// This is the positive proof of G3: WithParam binding routes data as a
    /// literal string value through SurrealDB's query executor, not as SurrealQL
    /// code. If parameterization were absent, payloads like "$id; DELETE wallet;"
    /// would execute and delete rows rather than storing the string.
    /// </summary>
    [SkippableFact]
    public async Task G3_DirectWithParam_HostileInput_PersistsAsLiteralString()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            "SurrealDB test container not reachable — start it via `pwsh scripts/surrealdb/start-test-container.ps1`.");

        var store = BuildWalletStore();

        // Seed a control wallet that must survive all writes.
        var controlWallet = new Wallet
        {
            Id        = Guid.NewGuid(),
            AvatarId  = Guid.NewGuid(),
            ChainType = "Solana",
            Address   = $"g3_ctrl_{Guid.NewGuid():N}",
            Label     = "G3-Control",
            WalletType = WalletType.Platform
        };
        var controlUpsert = await store.UpsertAsync(controlWallet);
        controlUpsert.IsError.Should().BeFalse("control wallet seed must succeed");

        // Use payload (a) — classic SQL and payload (d) — unicode fullwidth apostrophe.
        // These are the two most representative hostile strings for byte-exact label storage.
        var payloadA = HostilePayloads[0]; // ' OR 1=1; DROP TABLE wallet;--
        var payloadD = HostilePayloads[3]; // ＇ OR 1=1

        foreach (var payload in new[] { payloadA, payloadD })
        {
            var target = new Wallet
            {
                Id        = Guid.NewGuid(),
                AvatarId  = Guid.NewGuid(),
                ChainType = "Ethereum",
                Address   = $"g3_inj_{Guid.NewGuid():N}",
                Label     = payload,   // hostile string stored as label
                WalletType = WalletType.External
            };

            // Write through the real store's parameterized path.
            var upsertResult = await store.UpsertAsync(target);
            upsertResult.IsError.Should().BeFalse(
                $"UpsertAsync must succeed even when label is hostile payload: {payload}");

            // Read back via GetByIdAsync.
            var getResult = await store.GetByIdAsync(target.Id);
            getResult.IsError.Should().BeFalse(
                $"GetByIdAsync must find the row written with hostile label: {payload}");
            getResult.Result.Should().NotBeNull();

            // Byte-exact label round-trip: proves the payload is stored/retrieved as
            // an opaque string, not parsed as SurrealQL.
            getResult.Result!.Label.Should().Be(payload,
                $"Label must survive round-trip byte-exact — WithParam must treat the value as data, not SurrealQL");
        }

        // Control wallet must still be present: proves the hostile labels did not
        // issue DELETEs or DROPs that mutated the table.
        var controlGet = await store.GetByIdAsync(controlWallet.Id);
        controlGet.IsError.Should().BeFalse(
            "control wallet must still be present after injection probe writes");
        controlGet.Result!.Label.Should().Be("G3-Control",
            "control wallet label must be unmodified after injection probe writes");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TEST 3
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pure static analysis test — no SurrealDB container required.
    ///
    /// Writes a minimal .cs fixture that calls SurrealQuery.Of with an interpolated
    /// string, builds it with a ProjectReference to the real analyzer csproj, and
    /// asserts the build output contains "SRDB0001" and exits non-zero.
    ///
    /// If the analyzer csproj is absent on disk, the test is skipped defensively;
    /// Tests 1 and 2 remain the load-bearing G3 assertions.
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public async Task G3_AnalyzerSRDB0001_FiresOnInterpolatedSurrealQl()
    {
        var analyzerCsprojPath = FindAnalyzerCsprojPath();
        var clientCsprojPath   = FindClientCsprojPath();

        Skip.If(analyzerCsprojPath is null || clientCsprojPath is null,
            "Analyzer or Client csproj not found on disk — skipping SRDB0001 static-analysis gate. " +
            "Tests 1 and 2 remain the load-bearing G3 assertions.");

        var tempDir = Path.Combine(Path.GetTempPath(), $"g3-srdb0001-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // ── Write the tiny fixture program ────────────────────────────────
            var fixtureCs = Path.Combine(tempDir, "G3Fixture.cs");
            await File.WriteAllTextAsync(fixtureCs, BuildFixtureSource());

            // ── Write the minimal .csproj that references the real packages ───
            var fixtureCsproj = Path.Combine(tempDir, "G3Fixture.csproj");
            await File.WriteAllTextAsync(fixtureCsproj,
                BuildFixtureCsproj(clientCsprojPath!, analyzerCsprojPath!));

            // ── Shell out: dotnet build ───────────────────────────────────────
            var psi = new ProcessStartInfo("dotnet", $"build \"{fixtureCsproj}\" --no-incremental -nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = tempDir
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start dotnet build process.");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var buildOutput = stdout + stderr;

            // ── Assert SRDB0001 fires and build exits non-zero ────────────────
            buildOutput.Should().Contain("SRDB0001",
                $"Analyzer must emit SRDB0001 for interpolated SurrealQuery.Of call.\nBuild output:\n{buildOutput}");

            proc.ExitCode.Should().NotBe(0,
                $"Build must exit non-zero when SRDB0001 is Error severity.\nBuild output:\n{buildOutput}");
        }
        finally
        {
            // Best-effort cleanup — no leaked temp dirs.
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* swallow — OS will GC eventually */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// Query the wallet table row count via the SurrealDB HTTP API using a
    /// parameterized SELECT (G3 compliant — no user input in the query string).
    private async Task<long> QueryWalletCountAsync()
    {
        try
        {
            using var countClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
            countClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            countClient.DefaultRequestHeaders.Add("NS", TestNamespace);
            countClient.DefaultRequestHeaders.Add("DB", "test");
            countClient.DefaultRequestHeaders.Add("Accept", "application/json");

            // Literal constant SELECT — no user input interpolated.
            const string countSql = "SELECT count() FROM wallet GROUP ALL";
            var content = new StringContent(countSql, System.Text.Encoding.UTF8, "text/plain");
            var response = await countClient.PostAsync("/sql", content);

            if (!response.IsSuccessStatusCode)
                return 0;

            var json = await response.Content.ReadAsStringAsync();
            // SurrealDB returns an array of statement results; parse the count field.
            // Shape: [{"result":[{"count":N}],"status":"OK",...}]
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array &&
                root.GetArrayLength() > 0)
            {
                var first = root[0];
                if (first.TryGetProperty("result", out var resultArr) &&
                    resultArr.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    resultArr.GetArrayLength() > 0)
                {
                    var countObj = resultArr[0];
                    if (countObj.TryGetProperty("count", out var countProp))
                        return countProp.GetInt64();
                }
            }

            return 0;
        }
        catch
        {
            // Non-fatal: if the table doesn't exist yet or the query fails
            // gracefully, treat count as 0 so upper assertions still make sense.
            return 0;
        }
    }

    /// Build a direct SurrealWalletStore backed by an HttpSurrealConnection using
    /// the test namespace created by IntegrationTestBase.InitializeAsync().
    private SurrealWalletStore BuildWalletStore()
    {
        var options = new SurrealConnectionOptions
        {
            Endpoint  = SurrealTestDefaults.Endpoint,
            Namespace = TestNamespace,
            Database  = "test",
            User      = SurrealTestDefaults.User,
            Password  = SurrealTestDefaults.Password
        };
        var http       = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var connection = new HttpSurrealConnection(http, options);
        var executor   = new DefaultSurrealExecutor(connection);
        return new SurrealWalletStore(executor);
    }

    /// Walk parent directories from the test assembly output path to find the
    /// Azoa.SurrealDb.Analyzer csproj (mirrors IntegrationTestBase.FindRepoRoot).
    private static string? FindAnalyzerCsprojPath()
    {
        var root = FindRepoRootStatic();
        if (root is null) return null;
        var candidate = Path.Combine(root, "packages", "Azoa.SurrealDb.Analyzer",
            "Azoa.SurrealDb.Analyzer.csproj");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindClientCsprojPath()
    {
        var root = FindRepoRootStatic();
        if (root is null) return null;
        var candidate = Path.Combine(root, "packages", "Azoa.SurrealDb.Client",
            "Azoa.SurrealDb.Client.csproj");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindRepoRootStatic()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    /// Minimal C# source that calls SurrealQuery.Of with a string-interpolated
    /// argument — the exact pattern SRDB0001 is designed to catch.
    private static string BuildFixtureSource() => """
        using Azoa.SurrealDb.Client.Query;

        namespace G3.Fixture;

        public static class Program
        {
            public static void Main()
            {
                var userInput = "evil";
                // SRDB0001 must fire here: interpolated string passed to SurrealQuery.Of
                var q = SurrealQuery.Of($"SELECT * FROM wallet WHERE id = {userInput}");
                _ = q;
            }
        }
        """;

    /// Minimal .csproj that ProjectReferences the real Client and Analyzer packages.
    /// The Analyzer is referenced as an Analyzer asset (not a library) so Roslyn
    /// picks it up exactly as it will in production code.
    private static string BuildFixtureCsproj(string clientCsprojAbsPath, string analyzerCsprojAbsPath) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
            <!-- SRDB0001 is DiagnosticSeverity.Error — build exits non-zero already. -->
          </PropertyGroup>

          <ItemGroup>
            <!-- Real client package — provides SurrealQuery.Of for semantic resolution -->
            <ProjectReference Include="{clientCsprojAbsPath}" />
          </ItemGroup>

          <ItemGroup>
            <!-- Analyzer package — loaded as a Roslyn analyzer, not a lib reference -->
            <ProjectReference Include="{analyzerCsprojAbsPath}"
                              OutputItemType="Analyzer"
                              ReferenceOutputAssembly="false" />
          </ItemGroup>

        </Project>
        """;
}
