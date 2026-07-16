using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

public sealed class NodeGovernanceControllerIntegrationTests : IntegrationTestBase
{
    public NodeGovernanceControllerIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task GetParameters_WithoutAuth_Returns401()
    {
        using var anonymous = Factory.CreateClient();

        var response = await anonymous.GetAsync("api/node-governance/parameters");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NodeTransparency_AllowsCredentialFreeBrowserReadsFromAnyOrigin()
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/node-transparency/current");
        request.Headers.Add("Origin", "https://independent-auditor.example");
        request.Headers.Add("X-Api-Key", "invalid-header-must-not-trigger-key-lookup");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "invalid-token-must-be-ignored");

        using var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal("*");
    }

    [Fact]
    public async Task GetFeeSchedule_WithoutAuth_Returns401()
    {
        using var anonymous = Factory.CreateClient();

        var response = await anonymous.GetAsync("api/node-governance/fee-schedule");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTreasuryDestination_WithoutAuth_Returns401()
    {
        using var anonymous = Factory.CreateClient();

        var response = await anonymous.GetAsync(
            "api/node-governance/treasury/Simulated/Devnet");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicTransparency_AnonymousReadsAreSanitizedCursorPagedAndCacheable()
    {
        var actorId = Guid.NewGuid();
        using var nodeGovernor = Factory.CreateNodeGovernClient(actorId);
        var beforeResponse = await nodeGovernor.GetAsync("api/node-governance/fee-schedule");
        var before = await ReadResultAsync<NodeFeeScheduleResponse>(beforeResponse);

        var firstUpdate = await nodeGovernor.PutAsJsonAsync(
            "api/node-governance/fee-schedule",
            new NodeFeeScheduleUpdateRequest
            {
                ExpectedVersion = before!.Result!.Version,
                Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "91" },
            },
            JsonOptions);
        firstUpdate.StatusCode.Should().Be(HttpStatusCode.OK, await firstUpdate.Content.ReadAsStringAsync());
        var firstSaved = await ReadResultAsync<NodeFeeScheduleResponse>(firstUpdate);

        var secondUpdate = await nodeGovernor.PutAsJsonAsync(
            "api/node-governance/fee-schedule",
            new NodeFeeScheduleUpdateRequest
            {
                ExpectedVersion = firstSaved!.Result!.Version,
                Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "92" },
            },
            JsonOptions);
        secondUpdate.StatusCode.Should().Be(HttpStatusCode.OK, await secondUpdate.Content.ReadAsStringAsync());

        using var anonymous = Factory.CreateClient();
        var currentResponse = await anonymous.GetAsync("api/node-transparency/current");
        currentResponse.StatusCode.Should().Be(HttpStatusCode.OK, await currentResponse.Content.ReadAsStringAsync());
        currentResponse.Headers.ETag.Should().NotBeNull();
        currentResponse.Headers.CacheControl!.Public.Should().BeTrue();
        var currentBody = await currentResponse.Content.ReadAsStringAsync();
        currentBody.Contains(actorId.ToString(), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        currentBody.Contains("updatedByAvatarId", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        currentBody.Should().Contain("\"flatBaseUnits\":\"92\"");
        var current = JsonSerializer.Deserialize<AZOAResult<NodeTransparencySnapshotResponse>>(
            currentBody,
            JsonOptions);
        current!.Result!.CryptographicHistoryProofAvailable.Should().BeFalse();

        var firstPageResponse = await anonymous.GetAsync("api/node-transparency/audit/fees?limit=1");
        firstPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, await firstPageResponse.Content.ReadAsStringAsync());
        var firstPageBody = await firstPageResponse.Content.ReadAsStringAsync();
        firstPageBody.Contains(actorId.ToString(), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        firstPageBody.Contains("scheduleJson", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        firstPageBody.Contains("actorAvatarId", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        var firstPage = JsonSerializer.Deserialize<AZOAResult<NodeTransparencyPageResponse<PublicNodeFeeAuditResponse>>>(
            firstPageBody,
            JsonOptions);
        firstPage!.Result!.Items.Should().ContainSingle();
        firstPage.Result.Items[0].Schedule.Mint.FlatBaseUnits.Should().Be("92");
        firstPage.Result.NextCursor.Should().NotBeNullOrWhiteSpace();

        var secondPageResponse = await anonymous.GetAsync(
            $"api/node-transparency/audit/fees?limit=1&cursor={Uri.EscapeDataString(firstPage.Result.NextCursor!)}");
        secondPageResponse.StatusCode.Should().Be(HttpStatusCode.OK, await secondPageResponse.Content.ReadAsStringAsync());
        var secondPage = await ReadResultAsync<NodeTransparencyPageResponse<PublicNodeFeeAuditResponse>>(
            secondPageResponse);
        secondPage!.Result!.Items.Should().ContainSingle();
        secondPage.Result.Items[0].Schedule.Mint.FlatBaseUnits.Should().Be("91");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "api/node-transparency/current");
        conditional.Headers.IfNoneMatch.Add(currentResponse.Headers.ETag!);
        var notModified = await anonymous.SendAsync(conditional);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        notModified.Headers.ETag.Should().Be(currentResponse.Headers.ETag);
        notModified.Headers.CacheControl!.Public.Should().BeTrue();
        notModified.Headers.Vary.Should().Contain("Accept-Encoding");

        using var strongConditional = new HttpRequestMessage(HttpMethod.Get, "api/node-transparency/current");
        strongConditional.Headers.TryAddWithoutValidation(
            "If-None-Match",
            currentResponse.Headers.ETag!.Tag);
        var weakEquivalent = await anonymous.SendAsync(strongConditional);
        weakEquivalent.StatusCode.Should().Be(HttpStatusCode.NotModified);

        var invalidCursor = await anonymous.GetAsync(
            "api/node-transparency/audit/fees?cursor=not-a-protected-cursor");
        invalidCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidCursor.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task PutParameters_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = new[] { "Algorand" },
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutFeeSchedule_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.PutAsJsonAsync("api/node-governance/fee-schedule", new NodeFeeScheduleUpdateRequest
        {
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "1" },
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutTreasuryDestination_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.PutAsJsonAsync(
            "api/node-governance/treasury",
            new NodeTreasuryDestinationUpdateRequest
            {
                Chain = "Simulated",
                Network = ChainNetwork.Devnet,
                Address = SimulatedBlockchainProvider.SimAddress("Simulated", "forbidden"),
            },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutParameters_AsOperatorWithoutNodeGovern_Returns403()
    {
        using var operatorOnly = Factory.CreateOperatorOnlyClient();

        var response = await operatorOnly.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = new[] { "Algorand" },
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutParameters_AsApiKey_Returns403()
    {
        var avatar = await SeedAvatarAsync();
        using var manager = Factory.CreateAuthenticatedClientForAvatar(avatar.Id, AzoaDappRoles.Manager);
        var rawKey = await MintApiKeyAsync(manager);
        using var apiKeyClient = Factory.CreateApiKeyClient(rawKey);

        var response = await apiKeyClient.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = new[] { "Algorand" },
        }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetFeeSchedule_AsApiKey_Returns403()
    {
        var avatar = await SeedAvatarAsync();
        using var manager = Factory.CreateAuthenticatedClientForAvatar(avatar.Id, AzoaDappRoles.Manager);
        var rawKey = await MintApiKeyAsync(manager);
        using var apiKeyClient = Factory.CreateApiKeyClient(rawKey);

        var response = await apiKeyClient.GetAsync("api/node-governance/fee-schedule");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutFeeSchedule_AsNodeGovernor_PersistsGetRoundTripsAndAudits()
    {
        var actorId = Guid.NewGuid();
        using var nodeGovernor = Factory.CreateNodeGovernClient(actorId);
        var beforeResponse = await nodeGovernor.GetAsync("api/node-governance/fee-schedule");
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await ReadResultAsync<NodeFeeScheduleResponse>(beforeResponse);
        var expectedVersion = before!.Result!.Version + 1;

        var put = await nodeGovernor.PutAsJsonAsync("api/node-governance/fee-schedule", new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = before.Result.Version,
            Mint = new NodeFeeScheduleEntryRequest
            {
                FlatBaseUnits = "7",
                Bps = 250,
            },
            Transfer = new NodeFeeScheduleEntryRequest
            {
                FlatBaseUnits = "3",
                Bps = 125,
            },
        }, JsonOptions);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await ReadResultAsync<NodeFeeScheduleResponse>(put);
        saved!.Result!.Version.Should().Be(expectedVersion);
        saved.Result.UpdatedByAvatarId.Should().Be(actorId);
        saved.Result.Mint.FlatBaseUnits.Should().Be("7");
        saved.Result.Mint.Bps.Should().Be(250);
        saved.Result.Transfer.FlatBaseUnits.Should().Be("3");
        saved.Result.Transfer.Bps.Should().Be(125);
        saved.Result.CreatedAt.Should().NotBeNull();
        saved.Result.CreatedAt.GetValueOrDefault().Should().NotBe(default);

        var get = await nodeGovernor.GetAsync("api/node-governance/fee-schedule");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var loaded = await ReadResultAsync<NodeFeeScheduleResponse>(get);
        loaded!.Result!.Version.Should().Be(expectedVersion);
        loaded.Result.Mint.FlatBaseUnits.Should().Be("7");
        loaded.Result.Mint.Bps.Should().Be(250);
        loaded.Result.Transfer.FlatBaseUnits.Should().Be("3");
        loaded.Result.Transfer.Bps.Should().Be(125);
        loaded.Result.CreatedAt.Should().Be(saved.Result.CreatedAt);

        var auditResponse = await nodeGovernor.GetAsync("api/node-governance/fee-audit?limit=10");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = await ReadResultAsync<List<NodeFeeAuditResponse>>(auditResponse);
        var entry = audit!.Result.Should().ContainSingle(a =>
            a.ActorAvatarId == actorId
            && a.Action == "ScheduleUpdated"
            && a.PreviousVersion == expectedVersion - 1
            && a.NewVersion == expectedVersion).Which;
        entry.ScheduleJson.Should().Contain("\"flatBaseUnits\":\"7\"");
        entry.ScheduleJson.Should().Contain("\"bps\":250");
    }

    [Fact]
    public async Task PutFeeSchedule_ConcurrentSameVersion_AllowsExactlyOneWriter()
    {
        using var firstGovernor = Factory.CreateNodeGovernClient(Guid.NewGuid());
        using var secondGovernor = Factory.CreateNodeGovernClient(Guid.NewGuid());
        var beforeResponse = await firstGovernor.GetAsync("api/node-governance/fee-schedule");
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await ReadResultAsync<NodeFeeScheduleResponse>(beforeResponse);

        var firstRequest = new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = before!.Result!.Version,
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "41" },
        };
        var secondRequest = new NodeFeeScheduleUpdateRequest
        {
            ExpectedVersion = before.Result.Version,
            Mint = new NodeFeeScheduleEntryRequest { FlatBaseUnits = "42" },
        };

        var responses = await Task.WhenAll(
            firstGovernor.PutAsJsonAsync(
                "api/node-governance/fee-schedule", firstRequest, JsonOptions),
            secondGovernor.PutAsJsonAsync(
                "api/node-governance/fee-schedule", secondRequest, JsonOptions));

        var responseSummary = string.Join(" | ", await Task.WhenAll(responses.Select(async r =>
            $"{(int)r.StatusCode}:{await r.Content.ReadAsStringAsync()}")));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, responseSummary);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1, responseSummary);

        var afterResponse = await firstGovernor.GetAsync("api/node-governance/fee-schedule");
        var after = await ReadResultAsync<NodeFeeScheduleResponse>(afterResponse);
        after!.Result!.Version.Should().Be(before.Result.Version + 1);
        after.Result.Mint.FlatBaseUnits.Should().BeOneOf("41", "42");

        var auditResponse = await firstGovernor.GetAsync("api/node-governance/fee-audit?limit=100");
        var audit = await ReadResultAsync<IEnumerable<NodeFeeAuditResponse>>(auditResponse);
        audit!.Result!.Count(row => row.NewVersion == before.Result.Version + 1)
            .Should().Be(1, "the losing fee CAS transaction must not append an audit row");
    }

    [Fact]
    public async Task PutTreasuryDestination_AsNodeGovernor_PersistsGetRoundTripsAndAudits()
    {
        var actorId = Guid.NewGuid();
        var address = SimulatedBlockchainProvider.SimAddress(
            "Simulated",
            $"node-treasury-{Guid.NewGuid():N}");
        using var nodeGovernor = Factory.CreateNodeGovernClient(actorId);

        var put = await nodeGovernor.PutAsJsonAsync(
            "api/node-governance/treasury",
            new NodeTreasuryDestinationUpdateRequest
            {
                Chain = "Simulated",
                Network = ChainNetwork.Devnet,
                Address = address,
            },
            JsonOptions);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await ReadResultAsync<NodeTreasuryDestinationResponse>(put);
        saved!.Result!.Chain.Should().Be("Simulated");
        saved.Result.Network.Should().Be(ChainNetwork.Devnet);
        saved.Result.Address.Should().Be(address);
        saved.Result.UpdatedByAvatarId.Should().Be(actorId);

        var get = await nodeGovernor.GetAsync(
            "api/node-governance/treasury/Simulated/Devnet");
        get.StatusCode.Should().Be(HttpStatusCode.OK, await get.Content.ReadAsStringAsync());
        var loaded = await ReadResultAsync<NodeTreasuryDestinationResponse>(get);
        loaded!.Result!.Version.Should().Be(saved.Result.Version);
        loaded.Result.Address.Should().Be(address);

        var conflict = await nodeGovernor.PutAsJsonAsync(
            "api/node-governance/treasury",
            new NodeTreasuryDestinationUpdateRequest
            {
                Chain = "Simulated",
                Network = ChainNetwork.Devnet,
                Address = SimulatedBlockchainProvider.SimAddress(
                    "Simulated",
                    $"stale-node-treasury-{Guid.NewGuid():N}"),
                ExpectedVersion = saved.Result.Version - 1,
            },
            JsonOptions);
        conflict.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            await conflict.Content.ReadAsStringAsync());

        var auditResponse = await nodeGovernor.GetAsync(
            "api/node-governance/treasury-audit?limit=10");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = await ReadResultAsync<List<NodeTreasuryAuditResponse>>(auditResponse);
        audit!.Result.Should().Contain(a =>
            a.ActorAvatarId == actorId
            && a.Action == "DestinationUpdated"
            && a.Chain == "Simulated"
            && a.Network == ChainNetwork.Devnet
            && a.NewVersion == saved.Result.Version
            && a.DestinationJson.Contains(address, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PutParameters_AsNodeGovernor_PersistsGetRoundTripsAndAudits()
    {
        var actorId = Guid.NewGuid();
        using var nodeGovernor = Factory.CreateNodeGovernClient(actorId);
        var beforeResponse = await nodeGovernor.GetAsync("api/node-governance/parameters");
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = await ReadResultAsync<NodeGovernanceParametersResponse>(beforeResponse);
        var expectedVersion = before!.Result!.Version + 1;

        var put = await nodeGovernor.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            ExpectedVersion = before.Result.Version,
            AllowedChains = new[] { " solana ", "Algorand", "SOLANA" },
            AllowedAssetTypes = Array.Empty<string>(),
        }, JsonOptions);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await ReadResultAsync<NodeGovernanceParametersResponse>(put);
        saved!.Result!.Version.Should().Be(expectedVersion);
        saved.Result.UpdatedByAvatarId.Should().Be(actorId);
        saved.Result.AllowedChains.Should().Equal("Algorand", "solana");
        saved.Result.AllowedAssetTypes.Should().BeEmpty();
        saved.Result.CreatedAt.Should().NotBeNull();
        saved.Result.CreatedAt.GetValueOrDefault().Should().NotBe(default);

        var get = await nodeGovernor.GetAsync("api/node-governance/parameters");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var loaded = await ReadResultAsync<NodeGovernanceParametersResponse>(get);
        loaded!.Result!.AllowedChains.Should().Equal("Algorand", "solana");
        loaded.Result.AllowedAssetTypes.Should().BeEmpty();
        loaded.Result.CreatedAt.Should().Be(saved.Result.CreatedAt);

        var auditResponse = await nodeGovernor.GetAsync("api/node-governance/audit?limit=10");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = await ReadResultAsync<List<NodeGovernanceAuditResponse>>(auditResponse);
        audit!.Result.Should().Contain(a =>
            a.ActorAvatarId == actorId
            && a.Action == "ParametersUpdated"
            && a.PreviousVersion == expectedVersion - 1
            && a.NewVersion == expectedVersion
            && a.AllowedChains != null
            && a.AllowedChains.SequenceEqual(new[] { "Algorand", "solana" })
            && a.AllowedAssetTypes != null
            && !a.AllowedAssetTypes.Any());

        var stale = await nodeGovernor.PutAsJsonAsync(
            "api/node-governance/parameters",
            new NodeGovernanceParametersUpdateRequest
            {
                ExpectedVersion = saved.Result.Version - 1,
                AllowedChains = new[] { "Stale" },
            },
            JsonOptions);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PutParameters_ConcurrentExpectedVersion_HasExactlyOneWinner()
    {
        using var governorA = Factory.CreateNodeGovernClient(Guid.NewGuid());
        using var governorB = Factory.CreateNodeGovernClient(Guid.NewGuid());
        var beforeResponse = await governorA.GetAsync("api/node-governance/parameters");
        var before = await ReadResultAsync<NodeGovernanceParametersResponse>(beforeResponse);
        var expectedVersion = before!.Result!.Version;

        var writeA = governorA.PutAsJsonAsync(
            "api/node-governance/parameters",
            new NodeGovernanceParametersUpdateRequest
            {
                ExpectedVersion = expectedVersion,
                AllowedChains = new[] { "Algorand" },
            },
            JsonOptions);
        var writeB = governorB.PutAsJsonAsync(
            "api/node-governance/parameters",
            new NodeGovernanceParametersUpdateRequest
            {
                ExpectedVersion = expectedVersion,
                AllowedChains = new[] { "Solana" },
            },
            JsonOptions);

        var responses = await Task.WhenAll(writeA, writeB);
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var afterResponse = await governorA.GetAsync("api/node-governance/parameters");
        var after = await ReadResultAsync<NodeGovernanceParametersResponse>(afterResponse);
        after!.Result!.Version.Should().Be(expectedVersion + 1);

        var auditResponse = await governorA.GetAsync("api/node-governance/audit?limit=100");
        var audit = await ReadResultAsync<IEnumerable<NodeGovernanceAuditResponse>>(auditResponse);
        audit!.Result!.Count(row => row.NewVersion == expectedVersion + 1)
            .Should().Be(1, "the losing CAS transaction must not append an audit row");
    }

    [Fact]
    public async Task PutParameters_WithNullAllowlists_PreservesUnrestrictedSemantics()
    {
        using var nodeGovernor = Factory.CreateNodeGovernClient();

        var put = await nodeGovernor.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = null,
            AllowedAssetTypes = null,
        }, JsonOptions);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await ReadResultAsync<NodeGovernanceParametersResponse>(put);
        saved!.Result!.AllowedChains.Should().BeNull();
        saved.Result.AllowedAssetTypes.Should().BeNull();
    }

    [Fact]
    public async Task Audit_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.GetAsync("api/node-governance/audit");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FeeAudit_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.GetAsync("api/node-governance/fee-audit");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TreasuryAudit_AsManager_Returns403()
    {
        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);

        var response = await manager.GetAsync("api/node-governance/treasury-audit");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutParameters_DisallowedAssetType_ThenHolonCreateFails()
    {
        using var nodeGovernor = Factory.CreateNodeGovernClient();
        var update = await nodeGovernor.PutAsJsonAsync("api/node-governance/parameters", new NodeGovernanceParametersUpdateRequest
        {
            AllowedChains = null,
            AllowedAssetTypes = new[] { "Song" },
        }, JsonOptions);
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        using var manager = Factory.CreateAuthenticatedClient(AzoaDappRoles.Manager);
        var create = await manager.PostAsJsonAsync("api/holon", new HolonCreateModel
        {
            Name = "governed-holon",
            Description = "should be denied by node governance",
            ProviderName = "SurrealDb",
            AssetType = "Badge",
        }, JsonOptions);

        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await create.Content.ReadAsStringAsync();
        body.Should().Contain("Node governance disallows");
        body.Should().Contain("Badge");
    }

    private async Task<string> MintApiKeyAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("api/apikey", new CreateApiKeyRequest
        {
            Name = "node-governance-route-probe",
            Scopes = AzoaScopes.WalletManage,
        }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var result = await ReadResultAsync<CreateApiKeyResponse>(response);
        return result!.Result!.Key;
    }
}
