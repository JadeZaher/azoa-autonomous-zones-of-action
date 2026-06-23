namespace AZOA.WebAPI.Sagas;

/// <summary>
/// The producer-side entry point: start a saga instance by enqueuing its first
/// forward step into the durable outbox. A real consumer calls this inside the
/// SAME DB transaction as the state change that triggers the saga
/// (transactional outbox — no dual-write, no broker). Generic: the caller
/// supplies only a saga name, a correlation key, and a typed payload.
///
/// <para>Adding a new consumer = register an <see cref="ISagaDefinition"/> +
/// its <see cref="IStepHandler{TPayload}"/>s and call <see cref="StartAsync{TPayload}"/>.
/// No core change — proven by the non-bridge sample saga in the tests.</para>
/// </summary>
public interface ISagaCoordinator
{
    /// <summary>
    /// Enqueue the first forward step of <paramref name="sagaName"/> for the
    /// given <paramref name="correlationKey"/> with the typed
    /// <paramref name="payload"/>. The step's stable idempotency key is derived
    /// deterministically (see <see cref="SagaKeys"/>).
    /// </summary>
    Task StartAsync<TPayload>(
        string sagaName, string correlationKey, TPayload payload, CancellationToken ct);
}

/// <summary>
/// Default coordinator over <see cref="ISagaRegistry"/> + <see cref="ISagaStore"/>.
/// </summary>
public sealed class SagaCoordinator : ISagaCoordinator
{
    private readonly ISagaRegistry _registry;
    private readonly ISagaStore _store;

    public SagaCoordinator(ISagaRegistry registry, ISagaStore store)
    {
        _registry = registry;
        _store = store;
    }

    public async Task StartAsync<TPayload>(
        string sagaName, string correlationKey, TPayload payload, CancellationToken ct)
    {
        var def = _registry.Find(sagaName)
            ?? throw new InvalidOperationException(
                $"No saga definition registered for '{sagaName}'.");

        if (def is not SagaDefinition concrete)
            throw new InvalidOperationException(
                $"Saga '{sagaName}' is not a {nameof(SagaDefinition)}.");

        var first = concrete.FirstForwardStep;
        var idemKey = SagaKeys.StepIdempotencyKey(correlationKey, first.Name);
        var payloadJson = SagaStep<TPayload>.Serialize(payload);

        await _store.EnqueueAsync(
            sagaName, first.Name, correlationKey, idemKey, payloadJson,
            isCompensation: false, ct);
    }
}
