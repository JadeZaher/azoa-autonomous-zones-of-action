using System.Collections;
using System.Reflection;
using System.Text.Json;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates;

/// <summary>Builds a row every column attribute admits ([Inside], [References], [Assert] regex families, [Required]); see Round4Gates/AGENTS.md §model-valid-rows.</summary>
internal static class ModelValidRow
{
    public static T Fill<T>(T row) where T : class
    {
        foreach (var property in row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.GetIndexParameters().Length == 0))
        {
            if (property.GetCustomAttribute<IdAttribute>() is not null || property.Name == "Id" && property.PropertyType == typeof(string))
            {
                property.SetValue(row, Guid.NewGuid().ToString("N"));
                continue;
            }
            var value = ValueFor(property);
            if (value is not null) property.SetValue(row, value);
        }
        return row;
    }

    /// <summary>A value the column's model attributes admit; null leaves the CLR default (optional columns stay NONE).</summary>
    private static object? ValueFor(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var inside = property.GetCustomAttribute<InsideAttribute>();
        var assert = property.GetCustomAttribute<AssertAttribute>()?.Expression ?? "";
        var optional = Nullable.GetUnderlyingType(property.PropertyType) is not null
            || (!type.IsValueType && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable);
        if (type.IsEnum)
            return inside is { Values.Length: > 0 } ? Enum.Parse(type, inside.Values[0]) : Enum.GetValues(type).GetValue(0);
        if (type == typeof(string))
        {
            if (property.GetCustomAttribute<ReferencesAttribute>() is { } reference)
                return $"{TableName(reference.Target)}:{Guid.NewGuid():N}";
            if (inside is { Values.Length: > 0 }) return inside.Values[0];
            if (assert.Contains("{64}", StringComparison.Ordinal)) return new string('a', 64);
            if (assert.Contains("[A-Z]{3}", StringComparison.Ordinal)) return "USD";
            if (assert.Contains("Devnet", StringComparison.Ordinal)) return "Devnet";
            if (assert.Contains("[0-9]", StringComparison.Ordinal)) return "1";
            return optional ? null : "fixture";
        }
        if (type == typeof(bool)) return false;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(decimal) || type == typeof(double)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(float))
            return optional ? null : Convert.ChangeType(1, type);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
        if (type == typeof(DateTime)) return DateTime.UtcNow;
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(JsonElement)) return JsonDocument.Parse("{}").RootElement.Clone();
        if (type.IsArray) return optional ? null : Array.CreateInstance(type.GetElementType()!, 0);
        if (typeof(IEnumerable).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null) return optional ? null : Activator.CreateInstance(type);
        if (type.IsClass && type.GetConstructor(Type.EmptyTypes) is not null && !optional) return Fill(Activator.CreateInstance(type)!);
        return null;
    }

    public static string TableName(Type type) => type.GetCustomAttribute<SurrealTableAttribute>()?.Name ?? type.Name;
}
