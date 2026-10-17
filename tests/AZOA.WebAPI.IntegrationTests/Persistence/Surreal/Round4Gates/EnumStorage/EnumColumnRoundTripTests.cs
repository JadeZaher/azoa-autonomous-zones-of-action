using System.Linq.Expressions;
using System.Reflection;
using AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.Interleaving;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal.Round4Gates.EnumStorage;

/// <summary>Every enum-typed POCO column: its [Inside] set equals the enum, and every member written by SurrealWriter is found by a typed equality read; see Round4Gates/AGENTS.md §enum-round-trip.</summary>
public sealed class EnumColumnRoundTripTests
{
    public static IEnumerable<object[]> EnumColumns() => Columns().Select(column => new object[] { column.Type.Name + "." + column.Property.Name });

    [Fact]
    public void Every_Inside_list_names_exactly_the_enum_members()
    {
        var drift = Columns()
            .Select(column => (column, inside: column.Property.GetCustomAttribute<InsideAttribute>()))
            .Where(pair => pair.inside is not null)
            .Where(pair => !pair.inside!.Values.ToHashSet().SetEquals(Enum.GetNames(EnumType(pair.column.Property))))
            .Select(pair => $"{pair.column.Type.Name}.{pair.column.Property.Name}: [Inside] {string.Join(",", pair.inside!.Values)} vs enum {string.Join(",", Enum.GetNames(EnumType(pair.column.Property)))}");
        drift.Should().BeEmpty("a member missing from [Inside] makes every write of that state fail the ASSERT; an extra literal is a state the code can never read back");
    }

    [Fact]
    public void Enum_columns_exist_to_check()
        => Columns().Should().HaveCountGreaterThan(30, "the gate must see the persisted enum surface; an empty scan would pass vacuously");

    [Theory]
    [MemberData(nameof(EnumColumns))]
    public async Task Every_member_written_by_SurrealWriter_is_found_by_a_typed_equality_read(string column)
    {
        var (type, property) = Columns().Single(candidate => candidate.Type.Name + "." + candidate.Property.Name == column);
        // Schemaless namespace: this gate isolates writer/predicate agreement; the ASSERT lists are the Fact above.
        await using var database = await InterleavingDatabase.CreateAsync();
        var check = typeof(EnumColumnRoundTripTests).GetMethod(nameof(RoundTripAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type);
        foreach (var member in Enum.GetValues(EnumType(property)))
            await (Task)check.Invoke(null, [database.Executor, property, member])!;
    }

    private static async Task RoundTripAsync<T>(ISurrealExecutor executor, PropertyInfo property, object member)
        where T : class, ISurrealRecord, new()
    {
        var row = new T();
        var id = Guid.NewGuid().ToString("N");
        IdProperty(typeof(T)).SetValue(row, id);
        property.SetValue(row, member);
        (await executor.ExecuteAsync(SurrealWriter.Create(row))).EnsureAllOk();

        var parameter = Expression.Parameter(typeof(T), "row");
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Equal(Expression.Property(parameter, property), Expression.Constant(member, property.PropertyType)), parameter);
        var found = await executor.QueryAsync<T>(SurrealQuery<T>.From().Where(predicate));
        found.Select(candidate => IdProperty(typeof(T)).GetValue(candidate)?.ToString())
            .Should().Contain(stored => stored != null && stored.EndsWith(id, StringComparison.Ordinal),
                $"{typeof(T).Name}.{property.Name} = {member} written by SurrealWriter must match the typed predicate that reads it");
    }

    private static IEnumerable<(Type Type, PropertyInfo Property)> Columns()
        => typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<SurrealTableAttribute>() is not null && typeof(ISurrealRecord).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null && !type.IsAbstract)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite && EnumTypeOrNull(property) is not null)
                .Select(property => (type, property)))
            .OrderBy(column => column.type.Name).ThenBy(column => column.property.Name);

    private static Type? EnumTypeOrNull(PropertyInfo property)
        => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) is { IsEnum: true } type ? type : null;

    private static Type EnumType(PropertyInfo property) => EnumTypeOrNull(property)!;

    private static PropertyInfo IdProperty(Type type)
        => type.GetProperties().FirstOrDefault(property => property.GetCustomAttribute<IdAttribute>() is not null)
            ?? type.GetProperty("Id")!;
}
