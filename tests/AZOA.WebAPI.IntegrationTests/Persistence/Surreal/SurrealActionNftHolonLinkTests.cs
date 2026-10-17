using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Governance;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces;
using DomainHolon = AZOA.WebAPI.Models.Holon;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using Receipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionLocalReceipt;
using Lifecycle = AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle;
using User = AZOA.WebAPI.Persistence.SurrealDb.Models.User;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealActionNftHolonLinkTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly Guid _actor = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealHolonStore _holons = null!;
    private SurrealActionLocalMutationStore _mutations = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "holon", "wallet", "consent_grant", "action_run",
            "action_node_execution", "action_local_receipt", "local_entity_mutation_lock", "node_governance_parameters", "node_governance_policy_lock");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _holons = new(_executor);
        _mutations = new(_executor);
        _services = new ServiceCollection().AddSingleton(NodeGovernanceGuard.Unrestricted).BuildServiceProvider();
        await Save(new User { Id = _actor.ToString("N"), Username = _actor.ToString("N"), Email = _actor + "@test.invalid",
            PasswordHash = "test", IsActive = true });
    }

    public async Task DisposeAsync()
    {
        _services?.Dispose();
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentAttachmentHasOneReceiptAndReplayDoesNotTouchChangedOwner()
    {
        var (input, reference, confirmed, holon) = await Command();
        var service = new ActionNftHolonLinkService(_services, _holons, _mutations, _executor);
        var outcomes = await Task.WhenAll(service.LinkAsync(reference, input, confirmed, default),
            service.LinkAsync(reference, input, confirmed, default));
        outcomes.Should().OnlyContain(outcome => outcome == null);
        var saved = (await _holons.GetByIdAsync(holon.Id)).Result!;
        saved.TokenId.Should().Be(confirmed.AssetId);
        saved.ChainId.Should().Be(confirmed.ChainType);
        var receipts = await _executor.QueryAsync<Receipt>(SurrealQuery<Receipt>.From());
        receipts.Should().ContainSingle();
        receipts[0].OutputJson.Should().Contain(confirmed.FinalTransactionId!);

        var changed = ActionLocalSnapshot.Copy(saved);
        changed.UserId = Guid.NewGuid();
        changed.TokenId = "later-owner-binding";
        (await _holons.UpsertAsync(changed)).IsError.Should().BeFalse();
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Where(seed => true).Set(row => row.IsActive, false).Build())).EnsureAllOk();
        (await service.LinkAsync(reference, input, confirmed, default)).Should().BeNull();
        (await _holons.GetByIdAsync(holon.Id)).Result!.TokenId.Should().Be("later-owner-binding");
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("owner")]
    [InlineData("cancel")]
    public async Task ChangeAfterPlanningRollsBackAttachmentAndReceipt(string change)
    {
        var (input, reference, confirmed, holon) = await Command();
        // Real store; the concurrent change lands between planning and the atomic commit.
        var intercepted = new ChangeBeforeCommit(_mutations, async ct =>
        {
            if (change == "cancel")
                (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<RunRow>(input.RunId.ToString("N")).Where(seed => true)
                    .Set(row => row.Status, RunRow.ActionRunStatusKind.Cancelled).Build(), ct)).EnsureAllOk();
            else
            {
                var changed = ActionLocalSnapshot.Copy((await _holons.GetByIdAsync(holon.Id, ct)).Result!);
                if (change == "owner") changed.UserId = Guid.NewGuid();
                else changed.TokenId = "concurrent-asset";
                (await _holons.UpsertAsync(changed, ct)).IsError.Should().BeFalse();
            }
        });
        var service = new ActionNftHolonLinkService(_services, _holons, intercepted, _executor);
        (await service.LinkAsync(reference, input, confirmed, default))!.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await _executor.QueryAsync<Receipt>(SurrealQuery<Receipt>.From())).Should().BeEmpty();
        (await _holons.GetByIdAsync(holon.Id)).Result!.TokenId.Should().NotBe(confirmed.AssetId);
    }

    [Fact]
    public async Task ExistingBindingWithoutReceiptCannotBeReinterpretedAsThisGrant()
    {
        var (input, reference, confirmed, holon) = await Command();
        holon.TokenId = confirmed.AssetId;
        holon.ChainId = confirmed.ChainType;
        (await _holons.UpsertAsync(holon)).IsError.Should().BeFalse();
        var service = new ActionNftHolonLinkService(_services, _holons, _mutations, _executor);
        (await service.LinkAsync(reference, input, confirmed, default))!.Status.Should().Be(ActionExternalOperationStatus.Ambiguous);
        (await _executor.QueryAsync<Receipt>(SurrealQuery<Receipt>.From())).Should().BeEmpty();
    }

    private async Task<(ActionFrozenNodeInput Input, ActionExternalOperationReference Reference, Lifecycle Confirmed, DomainHolon Holon)> Command()
    {
        var run = Guid.NewGuid(); var node = Guid.NewGuid();
        var holon = new DomainHolon { Id = Guid.NewGuid(), UserId = _actor, Name = "Grant attachment", ProviderName = "AZOA",
            IsActive = true, CreatedDate = DateTime.UtcNow };
        (await _holons.UpsertAsync(holon)).IsError.Should().BeFalse();
        var config = new GrantNodeConfig { HolonId = holon.Id };
        var input = new ActionFrozenNodeInput(1, run, node, ActionNodeType.Grant, _actor, null,
            JsonSerializer.SerializeToElement(config), new Dictionary<Guid, ActionNodeExecution>(), new Dictionary<Guid, ActionNodeExecution>());
        var frozen = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(input));
        var hash = CanonicalJson.HashCanonical(frozen);
        await Save(new RunRow { Id = run.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            ActionId = "action:" + Guid.NewGuid().ToString("N"), Status = RunRow.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow });
        await Save(new ExecutionRow { Id = Guid.NewGuid().ToString("N"), RunId = "action_run:" + run.ToString("N"),
            NodeId = "action_node:" + node.ToString("N"), State = ExecutionRow.ActionNodeState.Running, FrozenInput = frozen, StartedAt = DateTimeOffset.UtcNow });
        var key = $"action:{run:N}:{node:N}:nft";
        var id = NftLifecycleIdentity.ForRequest(_actor, "Mint", key);
        var command = new NftLifecycleCommand(_actor, _actor, Guid.NewGuid(), Guid.NewGuid(), "actor", "actor", new(), -1, Guid.Empty,
            ActionAuthority: new(run, node, _actor, null, hash));
        var json = JsonSerializer.Serialize(command);
        var confirmed = new Lifecycle { Id = id, ActorUserId = _actor.ToString("N"), NftId = Guid.NewGuid().ToString("N"),
            Operation = "Mint", State = "Completed", ChainType = "Algorand", Network = "Devnet", ProtocolIdentity = "fixture-genesis",
            ContractAddress = "", AssetId = "42", FinalTransactionId = "confirmed-fixture-transaction", RequestJson = json,
            RequestHash = NftLifecycleIdentity.Fingerprint(json) };
        return (input, new(1, "nft", key, id, hash, _actor, null), confirmed, holon);
    }

    private async Task Save<T>(T row) where T : ISurrealRecord, new()
        => (await _executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();

    /// <summary>Delegates to the real store and runs <paramref name="change"/> just before the atomic commit.</summary>
    private sealed class ChangeBeforeCommit(IActionLocalMutationStore inner, Func<CancellationToken, Task> change) : IActionLocalMutationStore
    {
        public Task<Receipt?> GetReceiptAsync(string commandId, CancellationToken ct = default) => inner.GetReceiptAsync(commandId, ct);

        public async Task<AZOAResult<Receipt>> CommitAsync(ActionLocalMutationPlan plan, Receipt receipt, long? expectedPolicyVersion,
            bool requiresWalletScope, CancellationToken ct = default)
        {
            await change(ct);
            return await inner.CommitAsync(plan, receipt, expectedPolicyVersion, requiresWalletScope, ct);
        }
    }
}
