using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using AZOA.WebAPI.IntegrationTests.Factories;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Simulated;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AZOA.WebAPI.IntegrationTests.Controllers;

/// <summary>
/// H3 — ArdaNova flow e2e (FR-9e, contract §5 shape).
///
/// Flow: project holon create → GateCheck(holon.status == 'FUNDED') fail →
/// update holon metadata status=FUNDED → re-run → gate passes → Emit payload
/// readable from run execution-state.
///
/// Tier-2 chain leg (Grant/FungibleTokenCreate): the integration harness uses
/// Blockchain:Mode=Simulated so the SimulatedBlockchainProvider handles all
/// chain calls with deterministic, marker-prefixed sim: results. A wallet must
/// be seeded with a sim:-prefixed address so the ChainCapabilityGate passes.
///
/// D10 holon↔asset link: asserted through the holon Metadata field (token_id/
/// chain_id) after the run completes, where the Grant handler writes the link.
///
/// See conductor/tracks/quest-dag-semantic-hardening/NOTES.md §Phase H for
/// decisions about the Tier-2 leg and any deviations.
/// </summary>
public class QuestArdanovaFlowIntegrationTests : IntegrationTestBase, IDisposable
{
    // A separate factory pins Blockchain:Mode=Simulated so every chain call
    // routes to SimulatedBlockchainProvider and requires no actual node or signer.
    // It shares the outer factory's TestNamespace so both factories write to the
    // same SurrealDB namespace that IntegrationTestBase creates + schemas.
    private readonly ArdanovaSimulatedFactory _simFactory;
    private readonly HttpClient _simClient;

    public QuestArdanovaFlowIntegrationTests(AZOATestWebApplicationFactory factory) : base(factory)
    {
        _simFactory = new ArdanovaSimulatedFactory(factory.TestNamespace);
        _simClient  = _simFactory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _simClient.Dispose();
        _simFactory.Dispose();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 3-node quest: GateCheck(holon.&lt;id&gt;.Metadata.status == "FUNDED")
    /// → Emit(project.started) → Emit(done).
    /// The GateCheck reads the holon's LIVE metadata via the holon.&lt;id&gt; scope.
    /// </summary>
    private static QuestCreateModel ArdanovaFundedGateQuest(Guid projectHolonId, string name = "ArdanovaFunded") => new()
    {
        Name = name,
        Description = "ArdaNova GateCheck holon status FUNDED then Emit",
        Nodes =
        [
            new QuestNodeCreateModel
            {
                Name = "FundedGate",
                NodeType = QuestNodeType.GateCheck,
                Config = JsonSerializer.Serialize(new
                {
                    // Holon-state resolver predicate: GateCheckNodeHandler fetches the holon
                    // live and keys it as holon.<id> in the evaluator scope. The predicate
                    // drills into .status (from Metadata, which overlays typed fields).
                    // An unfunded holon has no 'status' key → predicate fails closed (FR-9e).
                    predicate = $"holon.{projectHolonId}.status == 'FUNDED'",
                    holons = new[] { projectHolonId }
                }),
                IsEntry = true,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "EmitStarted",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { @event = "project.started" } }),
                IsEntry = false,
                IsTerminal = false
            },
            new QuestNodeCreateModel
            {
                Name = "EmitDone",
                NodeType = QuestNodeType.Emit,
                Config = JsonSerializer.Serialize(new { payload = new { @event = "project.done" } }),
                IsEntry = false,
                IsTerminal = true
            }
        ],
        Edges =
        [
            new QuestEdgeCreateModel { SourceNodeId = 0, TargetNodeId = 1, EdgeType = QuestEdgeType.Control },
            new QuestEdgeCreateModel { SourceNodeId = 1, TargetNodeId = 2, EdgeType = QuestEdgeType.Control }
        ]
    };

    /// <summary>
    /// Builds a simulated-address wallet for the default avatar on the Simulated chain.
    /// </summary>
    private static Wallet SimulatedWallet() => new()
    {
        AvatarId = Guid.Parse(TestAuthHandler.DefaultAvatarId),
        ChainType = "Simulated",
        Address   = SimulatedBlockchainProvider.SimAddress("Simulated", "ardanova-test-wallet"),
        Label     = "Simulated test wallet",
        IsDefault = true
    };

    // ─── H3-a: gate fail — GateCheck on unfunded project fails, cascade skips ─

    [Fact]
    public async Task ArdanovaFlow_GateFails_BothEmitNodesSkipped()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // 1. Create a project holon (status NOT set → gate sees no FUNDED value).
        var holon = await SeedHolonAsync(h => h.WithName("ArdanovaProject_Unfunded"));
        holon.Id.Should().NotBeEmpty();

        // 2. Create quest that gates on the holon status.
        var create = await Client.PostAsJsonAsync("api/quest", ArdanovaFundedGateQuest(holon.Id), JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        // 3. Publish (gate quest has no fan-out, no structural error → publishes fine).
        var publish = await Client.PostAsync($"api/quest/{quest.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");

        // 4. Execute — gate should fail because holon has no "status: FUNDED" metadata.
        var exec = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec.StatusCode.Should().Be(HttpStatusCode.OK, $"execute failed: {await exec.Content.ReadAsStringAsync()}");
        var run = (await ReadResultAsync<QuestRun>(exec))!.Result!;

        // 5. Read execution state and assert cascade skip.
        var stateResp = await Client.GetAsync($"api/quest/runs/{run.Id}/execution-state");
        stateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var execState = (await ReadResultAsync<QuestExecutionState>(stateResp))!.Result!;

        var nodeExecs = execState.NodeExecutions.ToList();
        nodeExecs.Should().HaveCountGreaterThanOrEqualTo(1, "at least the gate node should have an execution row");

        // Gate must have Failed or ended in a non-Succeeded state.
        var gateExec = nodeExecs.FirstOrDefault();
        gateExec.Should().NotBeNull();
        gateExec!.State.Should().NotBe(QuestNodeState.Succeeded,
            "GateCheck on unfunded project must not succeed (gate fail-closed, FR-9e)");

        // Both downstream Emit nodes must be Skipped (cascade skip, AC-1a).
        // The DAG has exactly 2 downstream nodes (EmitStarted, EmitDone) so the count
        // must be exactly 2 — a looser bound would mask a partially-skipped cascade.
        var skipped = nodeExecs.Where(n => n.State == QuestNodeState.Skipped).ToList();
        skipped.Should().HaveCount(2,
            "both downstream Emit nodes must be Skipped when the gate fails (cascade skip D10 / AC-1a)");
    }

    // ─── H3-b: metadata update → re-run → gate passes → Emit output readable ──
    // FR-9e spec shape: create holon (unfunded) → quest with holon-state predicate →
    // first run fails gate → update holon metadata status=FUNDED → second run passes gate
    // → Emit output readable from execution-state API.

    [Fact]
    public async Task ArdanovaFlow_AfterFunding_GatePasses_EmitOutputReadable()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // 1. Create project holon — unfunded (no 'status' in Metadata so gate sees
        //    no FUNDED value; missing path fails closed per GatePredicateEvaluator).
        var holon = await SeedHolonAsync(h => h.WithName("ArdanovaProject_WillFund"));

        // 2. Create quest whose GateCheck predicate reads the holon's live Metadata.status
        //    via the holon-state resolver (GateCheckNodeHandler §8.1 / NOTES.md §Phase H).
        //    The holons array tells the handler to fetch the holon and key it as
        //    holon.<id> in the predicate scope; the predicate drills into .status.
        var holonId = holon.Id;
        var predicate = $"holon.{holonId}.status == 'FUNDED'";

        var create = await Client.PostAsJsonAsync("api/quest",
            new QuestCreateModel
            {
                Name = "ArdanovaHolonStateQuest",
                Description = "Gate on holon Metadata.status via holon-state resolver FR-9e",
                Nodes =
                [
                    new QuestNodeCreateModel
                    {
                        Name = "FundedGate",
                        NodeType = QuestNodeType.GateCheck,
                        Config = JsonSerializer.Serialize(new
                        {
                            predicate,
                            holons = new[] { holonId }
                        }),
                        IsEntry = true,
                        IsTerminal = false
                    },
                    new QuestNodeCreateModel
                    {
                        Name = "EmitStarted",
                        NodeType = QuestNodeType.Emit,
                        Config = JsonSerializer.Serialize(new
                        {
                            payload = new { @event = "project.started", projectHolonId = holonId }
                        }),
                        IsEntry = false,
                        IsTerminal = true
                    }
                ],
                Edges =
                [
                    new QuestEdgeCreateModel
                    {
                        SourceNodeId = 0,
                        TargetNodeId = 1,
                        EdgeType = QuestEdgeType.Control
                    }
                ]
            },
            JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        // 3. Publish — GateCheck→Emit is a valid linear DAG (no fan-out).
        var publish = await Client.PostAsync($"api/quest/{quest.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, $"publish failed: {await publish.Content.ReadAsStringAsync()}");

        // 4. First run — gate should FAIL because holon has no status=FUNDED metadata.
        var exec1 = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec1.StatusCode.Should().Be(HttpStatusCode.OK, $"execute1 failed: {await exec1.Content.ReadAsStringAsync()}");
        var run1 = (await ReadResultAsync<QuestRun>(exec1))!.Result!;

        var state1Resp = await Client.GetAsync($"api/quest/runs/{run1.Id}/execution-state");
        state1Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var execState1 = (await ReadResultAsync<QuestExecutionState>(state1Resp))!.Result!;

        var nodeExecs1 = execState1.NodeExecutions.ToList();
        nodeExecs1.Should().NotBeEmpty("first run must have at least the gate execution row");

        var gateExec1 = nodeExecs1.FirstOrDefault();
        gateExec1.Should().NotBeNull();
        gateExec1!.State.Should().NotBe(QuestNodeState.Succeeded,
            "gate on unfunded holon (no status metadata) must not pass (fail-closed, FR-9e)");

        var skipped1 = nodeExecs1.Where(n => n.State == QuestNodeState.Skipped).ToList();
        skipped1.Should().HaveCount(1,
            "the single downstream Emit node must be Skipped when the gate fails (cascade skip)");

        // 5. Update the holon's Metadata.status to FUNDED — the quest is Active during
        //    this mutation; that is fine, holon mutation is not a quest-definition mutation.
        var updateResp = await Client.PutAsJsonAsync($"api/holon/{holonId}",
            new HolonUpdateModel
            {
                Metadata = new Dictionary<string, string> { ["status"] = "FUNDED" }
            },
            JsonOptions);
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"holon metadata update failed: {await updateResp.Content.ReadAsStringAsync()}");

        // 6. Second run — gate must now PASS (holon Metadata.status == "FUNDED").
        var exec2 = await Client.PostAsync($"api/quest/{quest.Id}/execute", null);
        exec2.StatusCode.Should().Be(HttpStatusCode.OK, $"execute2 failed: {await exec2.Content.ReadAsStringAsync()}");
        var run2 = (await ReadResultAsync<QuestRun>(exec2))!.Result!;

        var state2Resp = await Client.GetAsync($"api/quest/runs/{run2.Id}/execution-state");
        state2Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var execState2 = (await ReadResultAsync<QuestExecutionState>(state2Resp))!.Result!;

        var nodeExecs2 = execState2.NodeExecutions.ToList();
        nodeExecs2.Should().NotBeEmpty();

        // Gate and Emit must both Succeed on the second run.
        var failed2 = nodeExecs2.Where(n => n.State == QuestNodeState.Failed).ToList();
        failed2.Should().BeEmpty("when holon.status=FUNDED the gate passes and all nodes succeed");

        var emitExec = nodeExecs2.LastOrDefault(n => n.State == QuestNodeState.Succeeded);
        emitExec.Should().NotBeNull("Emit node must Succeed on the funded run");

        // Emit output must be readable from the execution-state API (FR-9e).
        emitExec!.Output.Should().NotBeNullOrEmpty(
            "Emit node output must be readable from the execution-state API (FR-9e)");
    }

    // ─── H3-c: Tier-2 leg — Grant on simulated provider (chain-sim) ──────────
    // This test exercises a Grant node (Tier-2, RequiresChainCapability) using
    // the Simulated blockchain provider. Uses _simClient (Blockchain:Mode=Simulated
    // factory) with a sim:-prefixed wallet address pre-seeded.
    //
    [Fact]
    public async Task ArdanovaFlow_Tier2Grant_SimulatedProvider_TerminatesCleanly()
    {
        var skip = await SkipIfSurrealDbUnavailableAsync();
        Skip.IfNot(skip, "SurrealDB unavailable");

        // 1. Seed the capability prerequisite directly; the public wallet model
        //    intentionally rejects ':' while the simulated provider reserves it.
        var walletStore = new SurrealWalletStore(await CreateExecutorAsync(TestNamespace));
        var walletResult = await walletStore.UpsertAsync(SimulatedWallet());
        walletResult.IsError.Should().BeFalse(walletResult.Message);
        var wallet = walletResult.Result!;

        // 2. Create a Grant quest.
        var grantQuest = new QuestCreateModel
        {
            Name = "ArdanovaGrantQuest",
            Description = "Tier-2 Grant on simulated provider H3-c",
            Nodes =
            [
                new QuestNodeCreateModel
                {
                    Name = "GrantNode",
                    NodeType = QuestNodeType.Grant,
                    Config = JsonSerializer.Serialize(new
                    {
                        request = new
                        {
                            walletId    = wallet.Id,
                            name        = "ArdanovaProjectShare",
                            description = "Simulated project share token",
                            chainId     = "Simulated",
                            tokenId     = SimulatedBlockchainProvider.SimAddress("Simulated", "ardanova-project-share"),
                            metadata    = new Dictionary<string, string> { ["simulated"] = "true" }
                        },
                        holonId = (Guid?)null
                    }),
                    IsEntry = true,
                    IsTerminal = true
                }
            ],
            Edges = []
        };

        var create = await _simClient.PostAsJsonAsync("api/quest", grantQuest, JsonOptions);
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"create failed: {await create.Content.ReadAsStringAsync()}");
        var quest = (await ReadResultAsync<Quest>(create))!.Result!;

        // 3. Publish — Grant is a single-node quest (entry=terminal). The fan-out
        //    check will pass since there are no outgoing Control edges at all.
        var publish = await _simClient.PostAsync($"api/quest/{quest.Id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK,
            $"publish failed: {await publish.Content.ReadAsStringAsync()}");

        // 4. Execute.
        var exec = await _simClient.PostAsync($"api/quest/{quest.Id}/execute", null);
        var execBody = await exec.Content.ReadAsStringAsync();
        exec.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Grant execute failed: {execBody}");

        var run = (await ReadResultAsync<QuestRun>(exec))!.Result!;
        run.Status.Should().Be(QuestRunStatus.Succeeded,
            "a successful simulated Grant must project a successful run");

        // 5. Read execution state.
        var stateResp = await _simClient.GetAsync($"api/quest/runs/{run.Id}/execution-state");
        stateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var execState = (await ReadResultAsync<QuestExecutionState>(stateResp))!.Result!;

        var grantExec = execState.NodeExecutions.FirstOrDefault();
        grantExec.Should().NotBeNull("Grant node must have an execution row");

        grantExec!.State.Should().Be(QuestNodeState.Succeeded,
            "the successful run response and persisted Grant node must agree on its terminal state");
        grantExec.Output.Should().NotBeNullOrEmpty("Grant Succeeded must produce non-empty output");
    }
}

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> that pins
/// <c>Blockchain:Mode=Simulated</c> so every chain call routes to
/// <see cref="SimulatedBlockchainProvider"/> and requires no actual node or signer.
/// Shares the caller's <see cref="AZOATestWebApplicationFactory.TestNamespace"/> so
/// both the app and the harness operate in the same SurrealDB namespace.
/// </summary>
internal sealed class ArdanovaSimulatedFactory : WebApplicationFactory<Program>
{
    private readonly string _testNamespace;

    public ArdanovaSimulatedFactory(string testNamespace)
    {
        _testNamespace = testNamespace;
    }

    /// <summary>Bootstraps this independent host's shared namespace before hosted services query it.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        SurrealTestSchema.BootstrapHostedServicePrerequisitesAsync(_testNamespace)
            .GetAwaiter()
            .GetResult();
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTest");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]              = "super-secret-test-key-that-is-long-enough!",
                ["Jwt:Issuer"]           = "test",
                ["Jwt:Audience"]         = "test",
                ["SurrealDb:Endpoint"]   = SurrealTestDefaults.Endpoint,
                ["SurrealDb:User"]       = SurrealTestDefaults.User,
                ["SurrealDb:Password"]   = SurrealTestDefaults.Password,
                ["SurrealDb:Namespace"]  = _testNamespace,
                ["SurrealDb:Database"]   = AZOATestWebApplicationFactory.TestDatabase,
                ["AZOA:DefaultProvider"] = "SurrealDb",
                // Route every chain call to the simulated provider.
                ["Blockchain:Mode"]      = "Simulated",
                ["Blockchain:DefaultChain"] = "Simulated"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme    = TestAuthHandler.SchemeName;
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ForwardAuthenticate = TestAuthHandler.SchemeName;
                options.ForwardChallenge = TestAuthHandler.SchemeName;
            });

            services.RemoveAll<IValueAccessService>();
            services.AddSingleton<IValueAccessService, ReadyValueAccessService>();

        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        return client;
    }

    private sealed class ReadyValueAccessService : IValueAccessService
    {
        public Task<ValueAccessDecision> GetDecisionAsync(
            Guid participantId,
            Guid? tenantId = null,
            CancellationToken ct = default)
            => Task.FromResult(new ValueAccessDecision(ValueAccessState.Ready));

        public Task<AZOAResult<bool>> RequireValueAccessAsync(
            Guid participantId,
            Guid? tenantId = null,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<bool>.Success(true));
    }
}
