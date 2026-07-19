using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed class SurrealBlockchainOperationReceiptStoreTests : IAsyncLifetime
{
    private readonly string _testNamespace = $"test{Guid.NewGuid():N}";
    private SurrealBlockchainOperationStore _store = null!;
    private HttpSurrealConnection _connection = null!;
    private HttpClient _httpClient = null!;
    private bool _surrealAvailable;

    public async Task InitializeAsync()
    {
        _surrealAvailable = await ProbeSurrealAsync();
        if (!_surrealAvailable)
            return;

        var options = new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint,
            Namespace = _testNamespace,
            Database = "test",
            User = SurrealTestDefaults.User,
            Password = SurrealTestDefaults.Password,
        };
        _httpClient = new HttpClient { BaseAddress = new Uri(SurrealTestDefaults.Endpoint) };
        _connection = new HttpSurrealConnection(_httpClient, options);
        _store = new SurrealBlockchainOperationStore(new DefaultSurrealExecutor(_connection));

        await SurrealTestSchema.BootstrapAsync(_testNamespace, "operation_log");
    }

    public async Task DisposeAsync()
    {
        if (!_surrealAvailable)
            return;

        try
        {
            await SurrealTestSchema.DropAsync(_testNamespace);
        }
        finally
        {
            _connection.Dispose();
            _httpClient.Dispose();
        }
    }

    [SkippableFact]
    public async Task UpsertThenLookupByCorrelation_RoundTripsInitiatorLinks()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var correlation = new string('b', 64);
        var initiatorAvatarId = Guid.NewGuid();
        var initiatorApiKeyId = Guid.NewGuid();
        var operation = new BlockchainOperation
        {
            AvatarId = Guid.NewGuid(),
            OperationType = "Mint",
            Status = OperationStatus.Pending,
            IdempotencyKey = correlation,
            InitiatorAvatarId = initiatorAvatarId,
            InitiatorApiKeyId = initiatorApiKeyId,
        };

        var created = await _store.UpsertAsync(operation);
        var loaded = await _store.GetByIdempotencyKeyAsync(correlation);

        created.IsError.Should().BeFalse();
        loaded.IsError.Should().BeFalse();
        loaded.Result.Should().NotBeNull();
        loaded.Result!.Id.Should().Be(operation.Id);
        loaded.Result.IdempotencyKey.Should().Be(correlation);
        loaded.Result.InitiatorAvatarId.Should().Be(initiatorAvatarId);
        loaded.Result.InitiatorApiKeyId.Should().Be(initiatorApiKeyId);
    }

    [SkippableFact]
    public async Task UpsertExistingOperation_PreservesReadOnlyReceiptCorrelation()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var correlation = new string('c', 64);
        var operation = new BlockchainOperation
        {
            AvatarId = Guid.NewGuid(),
            OperationType = "Mint",
            Status = OperationStatus.Pending,
            IdempotencyKey = correlation,
            InitiatorAvatarId = Guid.NewGuid(),
        };

        var created = await _store.UpsertAsync(operation);
        operation.Status = OperationStatus.Completed;
        operation.CompletedDate = DateTime.UtcNow;
        var updated = await _store.UpsertAsync(operation);
        var loaded = await _store.GetByIdempotencyKeyAsync(correlation);

        created.IsError.Should().BeFalse();
        updated.IsError.Should().BeFalse();
        loaded.IsError.Should().BeFalse();
        loaded.Result.Should().NotBeNull();
        loaded.Result!.IdempotencyKey.Should().Be(correlation);
        loaded.Result.Status.Should().Be(OperationStatus.Completed);
    }

    [SkippableFact]
    public async Task Upsert_RawReceiptKey_IsRejectedBySchema()
    {
        Skip.IfNot(_surrealAvailable, "SurrealDB test container not available on " + SurrealTestDefaults.Endpoint);

        var operation = new BlockchainOperation
        {
            AvatarId = Guid.NewGuid(),
            OperationType = "Mint",
            Status = OperationStatus.Pending,
            IdempotencyKey = "payment-intent-secret",
        };

        var result = await _store.UpsertAsync(operation);

        result.IsError.Should().BeTrue();
    }

    private static async Task<bool> ProbeSurrealAsync()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await probe.GetAsync($"{SurrealTestDefaults.Endpoint}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
