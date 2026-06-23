namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Immutable registry over every DI-registered <see cref="ISagaDefinition"/>.
/// The processor resolves a record's definition by its stored saga name — it
/// never knows the concrete saga types, so a new consumer is purely additive
/// (register a definition + its handlers; zero core change).
/// </summary>
public sealed class SagaRegistry : ISagaRegistry
{
    private readonly IReadOnlyDictionary<string, ISagaDefinition> _byName;

    public SagaRegistry(IEnumerable<ISagaDefinition> definitions)
    {
        var map = new Dictionary<string, ISagaDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions)
        {
            if (!map.TryAdd(d.Name, d))
                throw new InvalidOperationException(
                    $"Duplicate saga definition registered for name '{d.Name}'.");
        }
        _byName = map;
    }

    public ISagaDefinition? Find(string sagaName) =>
        _byName.TryGetValue(sagaName, out var d) ? d : null;
}
