using System.Text.Json;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Http.Infrastructure;

/// <summary>
/// Cross-stack drift guard: the MSW fixtures in <c>frontend/src/test/msw/fixtures/</c> must keep
/// the shape the live API serializes. See <c>Http/AGENTS.md</c> §fixture-contract.
/// </summary>
public static class FrontendFixtureContract
{
    /// <summary>Loads one named object from a fixture file such as <c>business-verification.json</c>.</summary>
    public static JsonElement Load(string fileName, string entryName)
    {
        var path = Path.Combine(RepositoryRoot(), "frontend", "src", "test", "msw", "fixtures", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(entryName).Clone();
    }

    /// <summary>
    /// Asserts <paramref name="live"/> and <paramref name="fixture"/> agree on property names, JSON
    /// value kinds (string, number, boolean, null, object, array) and, recursively, nested objects
    /// and array element shapes in both directions (every live element and every fixture element).
    /// A fixture that cannot pin an element shape (an empty array where the live payload has
    /// elements) fails too.
    /// </summary>
    public static void ShouldMatchShape(JsonElement live, JsonElement fixture, string at = "$")
    {
        Kind(fixture).Should().Be(Kind(live), $"the frontend fixture at {at} must carry the API's JSON value kind");
        switch (live.ValueKind)
        {
            case JsonValueKind.Object:
                var liveNames = live.EnumerateObject().Select(property => property.Name).Order().ToArray();
                var fixtureNames = fixture.EnumerateObject().Select(property => property.Name).Order().ToArray();
                liveNames.Should().Equal(fixtureNames, $"the frontend fixture at {at} must mirror the API payload");
                foreach (var property in live.EnumerateObject())
                    ShouldMatchShape(property.Value, fixture.GetProperty(property.Name), $"{at}.{property.Name}");
                break;
            case JsonValueKind.Array:
                if (live.GetArrayLength() == 0) break;
                fixture.GetArrayLength().Should().BePositive($"the frontend fixture at {at} needs an element to pin the API's element shape");
                // Every live element against the fixture's shape, and every fixture element against the live shape.
                var index = 0;
                foreach (var element in live.EnumerateArray())
                    ShouldMatchShape(element, fixture[0], $"{at}[{index++}]");
                index = 0;
                foreach (var element in fixture.EnumerateArray())
                    ShouldMatchShape(live[0], element, $"{at}[fixture {index++}]");
                break;
        }
    }

    /// <summary>True and False are one kind: a boolean whose value differs is not drift.</summary>
    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        var kind => kind.ToString(),
    };

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (AZOA.WebAPI.csproj) not found above " + AppContext.BaseDirectory);
    }
}
