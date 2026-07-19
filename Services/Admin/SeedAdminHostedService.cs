using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.Options;
using SurrealForge.Client;

namespace AZOA.WebAPI.Services.Admin;

/// <summary>Validates legacy bootstrap config and seeds or rotates the durable node operator.</summary>
public sealed class SeedAdminHostedService : IHostedService
{
    private readonly AdminBootstrapOptions _legacy;
    private readonly NodeOperatorOptions _operator;
    private readonly IHostEnvironment _environment;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SeedAdminHostedService> _logger;

    public SeedAdminHostedService(
        IOptions<AdminBootstrapOptions> legacy,
        IOptions<NodeOperatorOptions> nodeOperator,
        IHostEnvironment environment,
        IServiceScopeFactory scopeFactory,
        ILogger<SeedAdminHostedService> logger)
    {
        _legacy = legacy.Value;
        _operator = nodeOperator.Value;
        _environment = environment;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateLegacyBootstrap();
        await SeedNodeOperatorAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidateLegacyBootstrap()
    {
        var hasEmail = !string.IsNullOrWhiteSpace(_legacy.SeedEmail);
        var hasSecret = !string.IsNullOrWhiteSpace(_legacy.SeedSecret);
        if (hasEmail == hasSecret)
            return;

        var message =
            "AdminBootstrap is misconfigured: SeedEmail and SeedSecret must both be set or both unset.";
        if (_environment.IsProduction())
            throw new InvalidOperationException(message);
        _logger.LogWarning("{Message}", message);
    }

    private async Task SeedNodeOperatorAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var stateStore = scope.ServiceProvider.GetRequiredService<IAdminBootstrapStateStore>();
        var avatarStore = scope.ServiceProvider.GetRequiredService<IAvatarStore>();
        var stateResult = await stateStore.GetAsync(ct);
        if (stateResult.IsError)
            throw new InvalidOperationException("Node operator binding could not be loaded.");

        var hasUsername = !string.IsNullOrWhiteSpace(_operator.Username);
        var hasPassword = !string.IsNullOrWhiteSpace(_operator.Password);
        var hasRevision = _operator.CredentialRevision > 0;
        var anySeed = hasUsername || hasPassword || hasRevision;
        var completeSeed = hasUsername && hasPassword && hasRevision;

        if (!anySeed)
        {
            if (stateResult.Result?.CredentialRevision > 0)
            {
                await AssertBoundOperatorAsync(stateResult.Result, avatarStore, ct);
                _logger.LogInformation(
                    "Durable node operator binding is active at credential revision {CredentialRevision}.",
                    stateResult.Result.CredentialRevision);
                return;
            }

            var message = "NodeOperator credentials are not configured and no durable operator binding exists.";
            if (_environment.IsProduction())
                throw new InvalidOperationException(message);
            _logger.LogWarning("{Message}", message);
            return;
        }

        if (!completeSeed)
            FailSeed("NodeOperator Username, Password, and CredentialRevision must all be set together.");
        var validation = NodeOperatorIdentity.Validate(_operator);
        if (validation is not null)
            FailSeed(validation);

        var username = NodeOperatorIdentity.NormalizeUsername(_operator.Username);
        var state = stateResult.Result;
        if (state is null)
        {
            await SeedFirstOperatorAsync(username, stateStore, avatarStore, ct);
            return;
        }

        if (state.CredentialRevision == 0)
        {
            throw new InvalidOperationException(
                "A legacy admin bootstrap binding already exists. Reset or explicitly migrate that binding before enabling NodeOperator credentials.");
        }

        var avatar = await AssertBoundOperatorAsync(state, avatarStore, ct);
        if (_operator.CredentialRevision < state.CredentialRevision)
            FailSeed("NodeOperator credential revision rollback was refused.");
        if (_operator.CredentialRevision == state.CredentialRevision)
        {
            if (!string.Equals(avatar.Username, username, StringComparison.Ordinal)
                || !NodeOperatorIdentity.VerifyPassword(_operator.Password, avatar.PasswordHash))
            {
                FailSeed("NodeOperator credentials differ without a revision increase.");
            }

            _logger.LogInformation(
                "Node operator seed matches durable credential revision {CredentialRevision}.",
                state.CredentialRevision);
            return;
        }

        var changedAt = DateTimeOffset.UtcNow;
        var rotated = await stateStore.RotateCredentialsAsync(
            NodeOperatorIdentity.AvatarId,
            username,
            BCrypt.Net.BCrypt.HashPassword(_operator.Password, workFactor: 12),
            state.CredentialRevision,
            _operator.CredentialRevision,
            changedAt,
            ct);
        if (rotated.IsError || rotated.Result is null)
            throw new InvalidOperationException(rotated.Message);

        _logger.LogInformation(
            "Node operator credentials rotated to revision {CredentialRevision}.",
            rotated.Result.CredentialRevision);
    }

    private async Task SeedFirstOperatorAsync(
        string username,
        IAdminBootstrapStateStore stateStore,
        IAvatarStore avatarStore,
        CancellationToken ct)
    {
        var avatar = new AZOA.WebAPI.Models.Avatar
        {
            Id = NodeOperatorIdentity.AvatarId,
            Username = username,
            Email = NodeOperatorIdentity.ReservedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_operator.Password, workFactor: 12),
            Title = "Node Operator",
            IsActive = true,
            IsVerified = true,
            DappRole = AzoaDappRoles.User,
            AuthNotBefore = DateTime.UtcNow,
        };
        var created = await avatarStore.CreateIfAbsentAsync(avatar, ct);
        if (created.IsError || created.Result is null
            || created.Result.Id != NodeOperatorIdentity.AvatarId
            || !string.Equals(created.Result.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(created.Result.Username, username, StringComparison.Ordinal)
            || !NodeOperatorIdentity.VerifyPassword(_operator.Password, created.Result.PasswordHash))
        {
            throw new InvalidOperationException("Reserved node operator identity could not be seeded safely.");
        }

        var now = DateTimeOffset.UtcNow;
        var binding = await stateStore.BindOnceAsync(new AdminBootstrapState
        {
            Id = AdminBootstrapState.LocalId,
            AvatarId = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(NodeOperatorIdentity.AvatarId)),
            CredentialRevision = _operator.CredentialRevision,
            SessionRevision = 1,
            CredentialUpdatedAt = now,
            ActivatedAt = now,
        }, ct);
        if (binding.IsError || binding.Result is null
            || binding.Result.CredentialRevision != _operator.CredentialRevision
            || !string.Equals(binding.Result.AvatarId, avatar.AvatarLink(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reserved node operator binding could not be created safely.");
        }

        _logger.LogInformation(
            "Node operator identity seeded at credential revision {CredentialRevision}.",
            binding.Result.CredentialRevision);
    }

    private static async Task<AZOA.WebAPI.Interfaces.IAvatar> AssertBoundOperatorAsync(
        AdminBootstrapState state,
        IAvatarStore avatarStore,
        CancellationToken ct)
    {
        var expectedLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(NodeOperatorIdentity.AvatarId));
        if (!string.Equals(state.AvatarId, expectedLink, StringComparison.Ordinal))
            throw new InvalidOperationException("The durable node operator binding does not reference the reserved operator identity.");

        var avatar = await avatarStore.GetByIdAsync(NodeOperatorIdentity.AvatarId, ct);
        if (avatar.IsError || avatar.Result is null
            || !avatar.Result.IsActive
            || !string.Equals(avatar.Result.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase)
            || !NodeOperatorIdentity.IsValidUsername(avatar.Result.Username)
            || !string.Equals(
                avatar.Result.Username,
                NodeOperatorIdentity.NormalizeUsername(avatar.Result.Username),
                StringComparison.Ordinal)
            || !NodeOperatorIdentity.IsStructurallyValidPasswordHash(avatar.Result.PasswordHash))
        {
            throw new InvalidOperationException("The durable node operator identity is missing or inactive.");
        }

        return avatar.Result;
    }

    private void FailSeed(string message)
    {
        if (_environment.IsProduction())
            throw new InvalidOperationException(message);
        throw new InvalidOperationException(message);
    }
}

file static class NodeOperatorAvatarExtensions
{
    public static string AvatarLink(this AZOA.WebAPI.Interfaces.IAvatar avatar)
        => SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatar.Id));
}
