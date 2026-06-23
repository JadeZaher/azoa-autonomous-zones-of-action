namespace AZOA.WebAPI.Sagas;

/// <summary>
/// A concrete saga definition: an ordered forward-step list plus any
/// compensation steps, indexed by name. Built once and registered as a
/// singleton — it is pure metadata (no state, no storage), so it is trivially
/// shareable and engine-portable.
///
/// <para>Construct with the fluent <see cref="SagaDefinitionBuilder"/>:
/// register forward steps in order, declare each forward step's compensation by
/// name, and add the compensation steps themselves. The bridge will be exactly
/// one of these in a later phase — nothing here is bridge-aware.</para>
/// </summary>
public sealed class SagaDefinition : ISagaDefinition
{
    private readonly IReadOnlyList<ISagaStep> _forward;
    private readonly IReadOnlyDictionary<string, ISagaStep> _byName;

    internal SagaDefinition(
        string name,
        IReadOnlyList<ISagaStep> forwardSteps,
        IReadOnlyDictionary<string, ISagaStep> allStepsByName)
    {
        Name = name;
        _forward = forwardSteps;
        _byName = allStepsByName;
    }

    public string Name { get; }

    public IReadOnlyList<ISagaStep> ForwardSteps => _forward;

    public ISagaStep? FindStep(string stepName) =>
        _byName.TryGetValue(stepName, out var s) ? s : null;

    public ISagaStep? NextForwardStep(string stepName)
    {
        for (var i = 0; i < _forward.Count; i++)
        {
            if (_forward[i].Name == stepName)
                return i + 1 < _forward.Count ? _forward[i + 1] : null;
        }
        return null;
    }

    /// <summary>The first forward step (where a new saga instance starts).</summary>
    public ISagaStep FirstForwardStep => _forward[0];

    public static SagaDefinitionBuilder Create(string name) => new(name);
}

/// <summary>
/// Fluent builder. Forward steps are appended in execution order; compensation
/// steps are added separately and referenced by name from a forward step.
/// Validates at <see cref="Build"/> that every declared compensation name
/// resolves to a registered step.
/// </summary>
public sealed class SagaDefinitionBuilder
{
    private readonly string _name;
    private readonly List<ISagaStep> _forward = new();
    private readonly Dictionary<string, ISagaStep> _byName = new(StringComparer.Ordinal);

    internal SagaDefinitionBuilder(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Saga name must be non-empty.", nameof(name));
        _name = name;
    }

    /// <summary>Append a forward step (execution order = call order).</summary>
    public SagaDefinitionBuilder AddForwardStep(ISagaStep step)
    {
        Register(step);
        _forward.Add(step);
        return this;
    }

    /// <summary>Add a compensation step (not part of the forward sequence; run
    /// only when a forward step that declares it exhausts retries).</summary>
    public SagaDefinitionBuilder AddCompensationStep(ISagaStep step)
    {
        Register(step);
        return this;
    }

    private void Register(ISagaStep step)
    {
        if (!_byName.TryAdd(step.Name, step))
            throw new InvalidOperationException(
                $"Saga '{_name}' already contains a step named '{step.Name}'.");
    }

    public SagaDefinition Build()
    {
        if (_forward.Count == 0)
            throw new InvalidOperationException(
                $"Saga '{_name}' must declare at least one forward step.");

        foreach (var s in _forward)
        {
            if (s.CompensationStepName is { } comp && !_byName.ContainsKey(comp))
                throw new InvalidOperationException(
                    $"Saga '{_name}' forward step '{s.Name}' declares compensation " +
                    $"'{comp}' which is not registered.");
        }

        return new SagaDefinition(_name, _forward.AsReadOnly(), _byName);
    }
}
