// SPDX-License-Identifier: UNLICENSED
using System.Net;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Proves the built-in database-role boundary against the pinned live engine.</summary>
public sealed class SurrealDatabaseRoleProofTests
{
    [SkippableFact]
    [Trait("Category", "SurrealDbFull")]
    public async Task Database_roles_prove_runtime_capabilities_and_schema_isolation_limit()
    {
        Skip.IfNot(await IsSurrealDbAvailableAsync(),
            "SurrealDB not reachable — start the local v3.1.4 dev stack before running the role proof.");

        var suffix = Guid.NewGuid().ToString("N");
        var database = $"roleproof{suffix}";
        var escapedNamespace = $"roleproofescape{suffix}";
        const string runtimeUser = "runtime";
        const string schemaUser = "schema";
        var runtimePassword = $"runtime{suffix}";
        var schemaPassword = $"schema{suffix}";

        try
        {
            // raw: isolated role/bootstrap proof; SurrealQL cannot bind DDL identifiers.
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                SurrealTestDefaults.User,
                SurrealTestDefaults.Password,
                $"DEFINE NAMESPACE {database}"));
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                SurrealTestDefaults.User,
                SurrealTestDefaults.Password,
                $"DEFINE DATABASE {database}", database));
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                SurrealTestDefaults.User,
                SurrealTestDefaults.Password,
                $"DEFINE USER {runtimeUser} ON DATABASE PASSWORD '{runtimePassword}' ROLES EDITOR",
                database,
                database));
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                SurrealTestDefaults.User,
                SurrealTestDefaults.Password,
                $"DEFINE USER {schemaUser} ON DATABASE PASSWORD '{schemaPassword}' ROLES OWNER",
                database,
                database));

            await EnsureSucceededAsync(await ExecuteSqlAsync(
                SurrealTestDefaults.User,
                SurrealTestDefaults.Password,
                "DEFINE TABLE runtime_probe SCHEMALESS; " +
                "DEFINE TABLE schema_ledger SCHEMAFULL; " +
                "DEFINE FIELD fingerprint ON schema_ledger TYPE string; " +
                "CREATE schema_ledger:baseline SET fingerprint = 'baseline'",
                database,
                database));

            // raw: transaction capability is a multi-statement invariant outside typed CRUD coverage.
            var runtimeTransaction = await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                "BEGIN TRANSACTION; " +
                "CREATE runtime_probe:entry SET value = 'created'; " +
                "UPDATE runtime_probe:entry SET value = 'updated'; " +
                "COMMIT TRANSACTION",
                database,
                database);
            await EnsureSucceededAsync(runtimeTransaction);
            runtimeTransaction.Payload.Should().Contain("updated");

            // A DB EDITOR can alter the schema and write the migration ledger. This is
            // intentionally asserted: it is the reason EDITOR alone is not a schema-tamper boundary.
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                "DEFINE FIELD runtime_editor_field ON schema_ledger TYPE option<string>",
                database,
                database));
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                "UPDATE schema_ledger:baseline SET fingerprint = 'runtime-mutated'",
                database,
                database));

            var mutatedLedger = await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                "SELECT fingerprint FROM schema_ledger:baseline",
                database,
                database);
            await EnsureSucceededAsync(mutatedLedger);
            mutatedLedger.Payload.Should().Contain("runtime-mutated");

            // Database scope prevents hierarchy and principal escalation even though it does not prevent DDL.
            await EnsureDeniedAsync(await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                $"DEFINE USER runtime_escalation ON DATABASE PASSWORD 'escalate{suffix}' ROLES OWNER",
                database,
                database));
            await EnsureDeniedAsync(await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                $"DEFINE DATABASE {escapedNamespace}",
                database,
                database));
            await EnsureDeniedAsync(await ExecuteSqlAsync(
                runtimeUser,
                runtimePassword,
                $"DEFINE NAMESPACE {escapedNamespace}"));

            // The schema identity has the authority the separate deployment job needs.
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                schemaUser,
                schemaPassword,
                "DEFINE TABLE schema_job_probe SCHEMALESS",
                database,
                database));
            await EnsureSucceededAsync(await ExecuteSqlAsync(
                schemaUser,
                schemaPassword,
                $"DEFINE USER schema_job_user ON DATABASE PASSWORD 'job{suffix}' ROLES VIEWER",
                database,
                database));
        }
        finally
        {
            // raw: isolated namespace teardown uses a server-generated identifier only.
            await RemoveNamespaceIfPresentAsync(database);
            await RemoveNamespaceIfPresentAsync(escapedNamespace);
        }
    }

    private static async Task<bool> IsSurrealDbAvailableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return (await client.GetAsync($"{SurrealTestDefaults.Endpoint}/health")).IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task RemoveNamespaceIfPresentAsync(string database)
    {
        var result = await ExecuteSqlAsync(
            SurrealTestDefaults.User,
            SurrealTestDefaults.Password,
            $"REMOVE NAMESPACE IF EXISTS {database}");

        if (result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || result.IsStatementError)
            throw new InvalidOperationException($"Failed to remove isolated role-proof namespace '{database}': {result.Payload}");
    }

    private static async Task<SqlResult> ExecuteSqlAsync(
        string user,
        string password,
        string sql,
        string? namespaceName = null,
        string? database = null)
    {
        using var client = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        if (namespaceName is not null)
            client.DefaultRequestHeaders.Add("Surreal-NS", namespaceName);
        if (database is not null)
            client.DefaultRequestHeaders.Add("Surreal-DB", database);
        if (!string.Equals(user, SurrealTestDefaults.User, StringComparison.Ordinal))
        {
            // System-user credentials default to root authentication. Database users
            // require a second, explicit auth scope in addition to the query scope.
            client.DefaultRequestHeaders.Add("Surreal-Auth-NS", namespaceName!);
            client.DefaultRequestHeaders.Add("Surreal-Auth-DB", database!);
        }

        using var response = await client.PostAsync(
            "/sql",
            new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        var payload = await response.Content.ReadAsStringAsync();
        return new SqlResult(response.StatusCode, payload, payload.Contains("\"status\":\"ERR\"", StringComparison.Ordinal));
    }

    private static Task EnsureSucceededAsync(SqlResult result)
    {
        ((int)result.StatusCode).Should().BeInRange(200, 299, result.Payload);
        result.IsStatementError.Should().BeFalse(result.Payload);
        return Task.CompletedTask;
    }

    private static Task EnsureDeniedAsync(SqlResult result)
    {
        (result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices || result.IsStatementError)
            .Should().BeTrue($"the database-scoped runtime identity must not escalate authority: {result.Payload}");
        return Task.CompletedTask;
    }

    private sealed record SqlResult(HttpStatusCode StatusCode, string Payload, bool IsStatementError);
}
