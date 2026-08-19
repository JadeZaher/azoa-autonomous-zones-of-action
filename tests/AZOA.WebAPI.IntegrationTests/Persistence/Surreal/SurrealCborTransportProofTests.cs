// SPDX-License-Identifier: UNLICENSED
// Positive proof that this application runs on SurrealForge's SDK/CBOR
// transport, not the legacy JSON one.
//
// Why this file exists: a suite that passes because it silently fell back to
// HttpSurrealConnection proves nothing. JSON let SurrealDB coerce a string into
// a `TYPE record<...>` / `TYPE decimal` column; CBOR does not. So "the tests are
// green" and "the FK columns hold record links" are two independent claims, and
// both are asserted here -- the second one through a raw HTTP fixture that does
// not go through SurrealForge at all.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealCborTransportProofTests : IntegrationTestBase
{
    public SurrealCborTransportProofTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    /// <summary>
    /// The application host itself -- not the test harness -- must resolve the
    /// SDK connection. This is the registration-level half of the proof: if
    /// Extensions/SurrealDbServiceCollectionExtensions ever reverts to
    /// hand-building HttpSurrealConnection, this fails immediately.
    /// </summary>
    [SkippableFact]
    public async Task App_host_serves_queries_over_the_sdk_cbor_connection()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            $"SurrealDB not reachable at {SurrealTestDefaults.Endpoint}.");

        using var scope = Factory.Services.CreateScope();

        var connection = scope.ServiceProvider.GetRequiredService<ISurrealConnection>();
        connection.Should().BeOfType<SurrealDbNetConnection>(
            "the app must run on SurrealDb.Net/CBOR, not the legacy JSON HttpSurrealConnection");

        // And it is genuinely serving traffic, not merely registered.
        var executor = scope.ServiceProvider.GetRequiredService<ISurrealExecutor>();
        var response = await executor.ExecuteAsync(SurrealQuery.Of("RETURN 1"), CancellationToken.None);
        response[0].IsOk.Should().BeTrue(response[0].ErrorText);
    }

    /// <summary>
    /// The wire-format half of the proof. <c>api_key.avatar_id</c> is declared
    /// <c>TYPE record&lt;avatar&gt;</c> and is backed by a C# <c>string</c>
    /// property carrying <c>[References(typeof(Avatar))]</c>; the CBOR
    /// marshaller promotes it to a native record id. The assertion is made with
    /// <c>type::of()</c> read back over a raw HTTP/JSON fixture, so nothing in
    /// SurrealForge participates in judging its own output.
    /// </summary>
    [SkippableFact]
    public async Task Foreign_key_column_holds_a_native_record_link_not_a_string()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            $"SurrealDB not reachable at {SurrealTestDefaults.Endpoint}.");

        var avatarId = Guid.NewGuid();
        var executor = await CreateExecutorAsync(TestNamespace);
        var store = new SurrealApiKeyStore(executor);

        var apiKey = new ApiKey
        {
            Id          = Guid.NewGuid(),
            AvatarId    = avatarId,
            Name        = "cbor-transport-proof",
            KeyHash     = $"hash-{Guid.NewGuid():N}",
            KeyPrefix   = "azoa_pk_",
            Scopes      = "read",
            CreatedDate = DateTime.UtcNow,
            IsActive    = true,
        };

        await store.CreateAsync(apiKey, CancellationToken.None);

        var wireType = await SelectScalarAsync(
            $"SELECT VALUE type::of(avatar_id) FROM api_key:{Format(apiKey.Id)}");

        wireType.Should().Be(
            "record",
            "a CBOR write must land a native record link in a TYPE record<avatar> column; " +
            "'string' here means the legacy JSON transport served the write");

        // The link must also resolve -- a dangling record id would also report
        // "record" while pointing at nothing.
        var linked = await SelectScalarAsync(
            $"SELECT VALUE <string>avatar_id FROM api_key:{Format(apiKey.Id)}");
        linked.Should().Be($"avatar:{Format(avatarId)}");
    }

    /// <summary>
    /// The other two write mechanisms, which do NOT go through the marshaller's
    /// property-level promotion but through the package's own record-link
    /// classifier (<c>SurrealForge.Client.Query.SurrealRecordLink</c>):
    /// <c>SurrealWriter.Create</c> (wallet, SET-based CREATE) and
    /// <c>SurrealWriter.Upsert</c> + a typed <c>Where</c> read-back (holon).
    /// Both the stored shape and the round-trip are asserted: a record link that
    /// writes correctly but cannot be queried back is the silent failure mode of
    /// this migration.
    /// </summary>
    [SkippableFact]
    public async Task Writer_and_predicate_paths_also_land_record_links()
    {
        Skip.IfNot(await SkipIfSurrealDbUnavailableAsync(),
            $"SurrealDB not reachable at {SurrealTestDefaults.Endpoint}.");

        var executor = await CreateExecutorAsync(TestNamespace);
        var avatarId = Guid.NewGuid();

        // SurrealWriter.Create -> wallet.avatar_id (TYPE record<avatar>)
        var walletStore = new SurrealWalletStore(executor);
        var wallet = new Wallet
        {
            Id          = Guid.NewGuid(),
            AvatarId    = avatarId,
            ChainType   = "Algorand",
            Address     = $"algo_{Guid.NewGuid():N}",
            Label       = "cbor-transport-proof",
            WalletType  = WalletType.Platform,
            CreatedDate = DateTime.UtcNow,
        };
        (await walletStore.UpsertAsync(wallet)).IsError.Should().BeFalse();

        (await SelectScalarAsync($"SELECT VALUE type::of(avatar_id) FROM wallet:{Format(wallet.Id)}"))
            .Should().Be("record");

        // ...and the typed predicate that reads it back must still match.
        var byAvatar = await walletStore.GetByAvatarAsync(avatarId);
        byAvatar.Result.Should().ContainSingle(
            "a record-link column must still be queryable by the store's own predicate");

        // SurrealWriter.Upsert -> holon.avatar_id / holon.parent_holon_id
        var holonStore = new SurrealHolonStore(executor);
        var holon = new Holon
        {
            Id            = Guid.NewGuid(),
            Name          = "cbor-transport-proof",
            Description   = "record-link proof",
            ParentHolonId = Guid.NewGuid(),
            AvatarId      = avatarId,
            ProviderName  = "SurrealProvider",
            ChainId       = "algorand",
            AssetType     = "NFT",
            CreatedDate   = DateTime.UtcNow,
            ModifiedDate  = DateTime.UtcNow,
            IsActive      = true,
        };
        (await holonStore.UpsertAsync(holon)).IsError.Should().BeFalse();

        foreach (var column in new[] { "avatar_id", "parent_holon_id" })
        {
            (await SelectScalarAsync($"SELECT VALUE type::of({column}) FROM holon:{Format(holon.Id)}"))
                .Should().Be("record", $"holon.{column} is TYPE option<record<...>>");
        }

        var byFilter = await holonStore.QueryAsync(new HolonQueryRequest { AvatarId = avatarId });
        byFilter.Result.Should().ContainSingle(
            "the composable typed filter must match a native record link");
    }

    private static string Format(Guid value) => value.ToString("N");

    /// <summary>
    /// Runs SurrealQL through the raw <c>/sql</c> HTTP endpoint with an
    /// <c>application/json</c> Accept header -- deliberately outside SurrealForge,
    /// so the readback cannot be produced by the same code path under test.
    /// </summary>
    private async Task<string?> SelectScalarAsync(string sql)
    {
        using var response = await SurrealClient.PostAsync(
            "/sql", new StringContent(sql, System.Text.Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var statement = document.RootElement[0];
        statement.GetProperty("status").GetString()
            .Should().Be("OK", statement.ToString());

        var rows = statement.GetProperty("result");
        rows.GetArrayLength().Should().BeGreaterThan(0, $"no rows came back for: {sql}");
        return rows[0].GetString();
    }
}
