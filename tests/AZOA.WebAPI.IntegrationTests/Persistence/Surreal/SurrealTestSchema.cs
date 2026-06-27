// SPDX-License-Identifier: UNLICENSED
// Shared test helper: bootstrap a per-test SurrealDB namespace + apply the REAL
// generated .surql golden(s) for the table(s) under test, instead of each test
// hand-maintaining inline DDL that drifts from the schema. Drift was the root
// cause of a wave of SurrealDB-3.x integration failures (FK columns typed
// `string` in the stale inline DDL vs `record<T>` in the real schema, stale
// FLEXIBLE/assert clauses, etc.). Applying the goldens keeps the tests honest.

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

internal static class SurrealTestSchema
{
    /// <summary>
    /// Create <paramref name="testNamespace"/> (+ database "test") and apply the
    /// committed Generated/Schemas/&lt;table&gt;.surql for each requested table.
    /// Identifier-safe Guid-hex namespace is interpolated (SurrealDB rejects a
    /// $param namespace identifier); DEFINE NAMESPACE runs at ROOT, the rest is
    /// scoped to NS+DB. The goldens are the SAME bytes the byte-equivalence test
    /// guards, so tests exercise the real schema.
    /// </summary>
    public static Task BootstrapAsync(string testNamespace, params string[] tableGoldenNames)
        => BootstrapWithExtraAsync(testNamespace, null, tableGoldenNames);

    /// <summary>
    /// As <see cref="BootstrapAsync(string, string[])"/>, plus an
    /// <paramref name="extraDdl"/> block applied after the goldens — for tables
    /// that have no committed golden (e.g. SCHEMALESS join tables). Distinct
    /// method name (not an overload) to avoid the params-vs-string? overload
    /// ambiguity that silently routed a single table name into extraDdl.
    /// </summary>
    public static async Task BootstrapWithExtraAsync(string testNamespace, string? extraDdl, params string[] tableGoldenNames)
    {
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));

        // 1. Namespace at ROOT (no NS/DB headers — the identifier is interpolated).
        using (var root = NewClient(credentials))
        {
            await PostAsync(root, $"DEFINE NAMESPACE IF NOT EXISTS {testNamespace}");
        }

        // 2. Everything else on ONE namespace-scoped connection (NS header only).
        //    The batch begins with `DEFINE DATABASE` + `USE DB test` so the table
        //    DDL lands in `test` — done on a SINGLE connection to avoid the
        //    cross-connection DB-visibility race that left tables unapplied
        //    (a Surreal-DB header pointing at a not-yet-existing db faults the
        //    connection-level USE, so we switch via the in-batch USE statement).
        var schemaDir = LocateSchemaDir();
        var batch = new StringBuilder();
        batch.AppendLine("DEFINE DATABASE IF NOT EXISTS test;");
        batch.AppendLine("USE DB test;");
        foreach (var table in tableGoldenNames)
        {
            var path = Path.Combine(schemaDir, table + ".surql");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Generated schema golden not found for table '{table}': {path}. " +
                    "Regenerate via AZOA_REGENERATE_GOLDENS=1 or check the table name.");
            batch.AppendLine(await File.ReadAllTextAsync(path));
            batch.AppendLine(";");
        }
        if (!string.IsNullOrWhiteSpace(extraDdl))
        {
            batch.AppendLine(extraDdl!);
            batch.AppendLine(";");
        }

        using var ddl = NewClient(credentials);
        ddl.DefaultRequestHeaders.Add("Surreal-NS", testNamespace);
        await PostAsync(ddl, batch.ToString());
    }

    /// <summary>Drop the test namespace at ROOT (best-effort).</summary>
    public static async Task DropAsync(string testNamespace)
    {
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{SurrealTestDefaults.User}:{SurrealTestDefaults.Password}"));
        using var root = NewClient(credentials);
        try { await PostAsync(root, $"REMOVE NAMESPACE IF EXISTS {testNamespace}"); }
        catch { /* best-effort teardown */ }
    }

    private static HttpClient NewClient(string credentials)
    {
        var c = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return c;
    }

    private static async Task PostAsync(HttpClient client, string surql)
    {
        var resp = await client.PostAsync("/sql",
            new StringContent(surql, System.Text.Encoding.UTF8, "text/plain"));
        // Surface DDL failures rather than swallowing them — a failed schema
        // apply must not masquerade as a passing (but tableless) test.
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode || body.Contains("\"status\":\"ERR\""))
            throw new InvalidOperationException(
                $"Test schema apply failed (HTTP {(int)resp.StatusCode}): {body}");
    }

    private static string LocateSchemaDir()
    {
        // Walk up from the test bin to the repo root, then Persistence/SurrealDb/Generated/Schemas.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "Persistence", "SurrealDb", "Generated", "Schemas");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException(
            "Could not locate Persistence/SurrealDb/Generated/Schemas from " + AppContext.BaseDirectory);
    }
}
