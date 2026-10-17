using System.Reflection;
using System.Text.Json.Serialization;
using FluentAssertions;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every enum column on a SurrealDB POCO stores its name through <see cref="JsonStringEnumConverter"/>; see Architecture/AGENTS.md §enum-storage.</summary>
public sealed class EnumStorageConverterTests
{
    [Fact]
    public void Every_persisted_enum_property_carries_a_string_enum_converter()
    {
        var offenders = typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<SurrealTableAttribute>() is not null)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType).IsEnum)
                .Where(property => !HasStringConverter(property))
                .Select(property => $"{type.FullName}.{property.Name}"))
            .ToList();

        offenders.Should().BeEmpty("a converter-less enum is written as its C# name by SurrealWriter but bound camelCase by typed reads, so typed filters miss writer-written rows");
    }

    private static bool HasStringConverter(PropertyInfo property)
    {
        var enumType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var converter = property.GetCustomAttribute<JsonConverterAttribute>() ?? enumType.GetCustomAttribute<JsonConverterAttribute>();
        return converter?.ConverterType is { } type && type.IsAssignableTo(typeof(JsonStringEnumConverter));
    }
}
