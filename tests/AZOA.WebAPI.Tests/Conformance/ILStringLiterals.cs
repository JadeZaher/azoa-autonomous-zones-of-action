using System.Reflection;

namespace AZOA.WebAPI.Tests.Conformance;

/// <summary>
/// Reads the string literals compiled into a type, including the nested state machines the
/// compiler generates for its async methods. The battery uses this to prove a contract that
/// has no runtime surface — that a provider with a broadcast path carries the
/// pending-confirmation marker — without reading source files.
/// </summary>
public static class ILStringLiterals
{
    private const byte LdstrOpcode = 0x72;
    private const uint UserStringTokenTag = 0x70000000;

    public static IReadOnlySet<string> In(Type type)
    {
        var literals = new HashSet<string>(StringComparer.Ordinal);
        Collect(type, literals, new HashSet<Type>());
        return literals;
    }

    /// <summary>True when any method of the type (or its nested types) contains the literal.</summary>
    public static bool Contains(Type type, string literal) => In(type).Contains(literal);

    private static void Collect(Type type, HashSet<string> literals, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return;

        const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        IEnumerable<MethodBase> members;
        try
        {
            members = type.GetMethods(MemberFlags).Cast<MethodBase>()
                .Concat(type.GetConstructors(MemberFlags));
        }
        catch (Exception)
        {
            return;
        }

        foreach (var member in members)
            CollectFrom(member, type.Module, literals);

        Type[] nested;
        try
        {
            nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in nested)
            Collect(child, literals, visited);
    }

    private static void CollectFrom(MethodBase method, Module module, HashSet<string> literals)
    {
        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            return;
        }

        if (il is null || il.Length < 5)
            return;

        // A deliberately naive ldstr scan rather than a full opcode walk: the only use is a
        // presence check, so a spurious extra literal is harmless while a missed one would be
        // a false alarm. Requiring the user-string token tag removes almost every false hit.
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != LdstrOpcode)
                continue;

            uint token = BitConverter.ToUInt32(il, i + 1);
            if ((token & 0xFF000000u) != UserStringTokenTag)
                continue;

            try
            {
                literals.Add(module.ResolveString((int)token));
            }
            catch (Exception)
            {
                // Not a real ldstr site; the byte pattern was operand data.
            }
        }
    }
}
