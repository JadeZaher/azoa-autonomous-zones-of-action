using System.Reflection;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Process-edge fake: answers only the named members and fails loudly on any other call.</summary>
public class InterfaceFake<T> : DispatchProxy where T : class
{
    private Dictionary<string, Func<MethodInfo, object?[], object?>> _members = new(StringComparer.Ordinal);

    /// <summary>Creates a fake whose members are keyed by method name (property getters use <c>get_Name</c>).</summary>
    public static T Create(Dictionary<string, Func<MethodInfo, object?[], object?>> members)
    {
        var proxy = Create<T, InterfaceFake<T>>();
        ((InterfaceFake<T>)(object)proxy)._members = members;
        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        return _members.TryGetValue(targetMethod.Name, out var member)
            ? member(targetMethod, args ?? [])
            : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod.Name} is outside this flow's process edge.");
    }
}
