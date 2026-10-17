using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using RunRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionRun;
using ExecutionRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionNodeExecution;
using DomainWallet = AZOA.WebAPI.Models.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealActionLocalMutationTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _tenant = Guid.NewGuid();
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealActionLocalMutationStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "user", "wallet", "holon", "consent_grant", "action_run",
            "action_node_execution", "action_local_receipt", "local_entity_mutation_lock", "node_governance_parameters",
            "node_governance_policy_lock");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions { Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _namespace, Database = "test", User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new(_executor);
        foreach (var id in new[] { _actor, _tenant })
            await Save(new User { Id = id.ToString("N"), Username = id.ToString("N"), Email = id + "@test.invalid",
                PasswordHash = "test", IsActive = true });
    }

    public async Task DisposeAsync()
    {
        try { await SurrealTestSchema.DropAsync(_namespace); }
        finally { _connection?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitGrantsAuthorizeOnlyWhileBothRequiredScopesAreLive(bool revokeWallet)
    {
        await Save(Grant(AzoaScopes.ActionExecute));
        var walletGrant = Grant(AzoaScopes.WalletManage);
        if (revokeWallet) walletGrant.RevokedAt = DateTimeOffset.UtcNow;
        await Save(walletGrant);
        var receipt = await Command();
        var wallet = NewWallet();
        var plan = new ActionLocalMutationPlan();
        plan.WalletReads[wallet.Id] = null;
        plan.WalletWrites[wallet.Id] = wallet;

        var result = await _store.CommitAsync(plan, receipt, null, true);
        result.IsError.Should().Be(revokeWallet, result.Message);
        var saved = await new SurrealWalletStore(_executor).GetByIdAsync(wallet.Id);
        saved.IsError.Should().Be(revokeWallet);
        (await _store.GetReceiptAsync(receipt.Id) is not null).Should().Be(!revokeWallet);
    }

    [Fact]
    public async Task ChangedQueryMembershipRollsBackEveryStagedWriteAndReceipt()
    {
        await Save(Grant(AzoaScopes.ActionExecute));
        await Save(Grant(AzoaScopes.WalletManage));
        var receipt = await Command();
        var first = NewWallet();
        var second = NewWallet();
        var plan = new ActionLocalMutationPlan();
        plan.WalletQueries.Add(new(_actor, []));
        plan.WalletReads[first.Id] = null;
        plan.WalletWrites[first.Id] = first;
        plan.WalletReads[second.Id] = null;
        plan.WalletWrites[second.Id] = second;
        var intruder = NewWallet();
        var wallets = new SurrealWalletStore(_executor);
        (await wallets.UpsertAsync(intruder)).IsError.Should().BeFalse();

        (await _store.CommitAsync(plan, receipt, null, true)).IsError.Should().BeTrue();
        (await wallets.GetByIdAsync(first.Id)).IsError.Should().BeTrue();
        (await wallets.GetByIdAsync(second.Id)).IsError.Should().BeTrue();
        (await _store.GetReceiptAsync(receipt.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RejectedCommandReceiptFencesSuspendedSuccessfulPlanner()
    {
        await Save(Grant(AzoaScopes.ActionExecute));
        await Save(Grant(AzoaScopes.WalletManage));
        var successful = await Command();
        var rejected = new ActionLocalReceipt { Id = successful.Id, RunId = successful.RunId, NodeId = successful.NodeId,
            ActorId = successful.ActorId, TenantId = successful.TenantId, IntentHash = successful.IntentHash,
            OutputJson = "null", IsError = true, Message = "Invalid frozen command", CreatedAt = successful.CreatedAt };
        (await _store.CommitAsync(new(), rejected, null, true)).IsError.Should().BeFalse();
        var wallet = NewWallet();
        var plan = new ActionLocalMutationPlan();
        plan.WalletReads[wallet.Id] = null;
        plan.WalletWrites[wallet.Id] = wallet;
        var replay = await _store.CommitAsync(plan, successful, null, true);

        replay.IsError.Should().BeFalse();
        replay.Result!.IsError.Should().BeTrue();
        (await new SurrealWalletStore(_executor).GetByIdAsync(wallet.Id)).IsError.Should().BeTrue();
    }

    private ConsentGrant Grant(string scope) => new() { Id = Guid.NewGuid().ToString("N"),
        GrantorUserId = "user:" + _actor.ToString("N"), TenantId = "user:" + _tenant.ToString("N"), Scopes = scope };

    private DomainWallet NewWallet() => new() { Id = Guid.NewGuid(), UserId = _actor, ChainType = "Solana",
        Address = Guid.NewGuid().ToString("N"), Label = "fixture" };

    private async Task<ActionLocalReceipt> Command()
    {
        var run = Guid.NewGuid().ToString("N");
        var node = Guid.NewGuid().ToString("N");
        const string frozen = "{\"fixture\":true}";
        await Save(new RunRow { Id = run, ActionId = "action:" + Guid.NewGuid().ToString("N"),
            UserId = "user:" + _actor.ToString("N"), ActingTenantId = "user:" + _tenant.ToString("N"),
            Status = RunRow.ActionRunStatusKind.Running, StartedAt = DateTimeOffset.UtcNow });
        await Save(new ExecutionRow { Id = Guid.NewGuid().ToString("N"), RunId = "action_run:" + run,
            NodeId = "action_node:" + node, State = ExecutionRow.ActionNodeState.Running, FrozenInput = frozen,
            StartedAt = DateTimeOffset.UtcNow });
        return new() { Id = Guid.NewGuid().ToString("N"), RunId = run, NodeId = node, ActorId = _actor.ToString("N"),
            TenantId = _tenant.ToString("N"), IntentHash = CanonicalJson.HashCanonical(frozen), OutputJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow };
    }

    private async Task Save<T>(T row) where T : ISurrealRecord
        => (await _executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();
}
