using System.Text.Json;
using AZOA.WebAPI.LiveTests.Models;

namespace AZOA.WebAPI.LiveTests.Parsers;

/// <summary>
/// Discovers and parses JSONL test payload files into test suites.
/// </summary>
public static class JsonlTestParser
{
    public static async Task<List<TestSuite>> DiscoverAndParseAsync(string directory, string pattern)
    {
        var suites = new List<TestSuite>();

        if (!Directory.Exists(directory))
            return suites;

        var files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                             .OrderBy(f => f)
                             .ToList();

        foreach (var file in files)
        {
            var suite = await ParseFileAsync(file);
            if (suite.Cases.Count > 0)
                suites.Add(suite);
        }

        return suites;
    }

    public static async Task<TestSuite> ParseFileAsync(string filePath)
    {
        var suite = new TestSuite
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            FilePath = filePath,
            Cases = new List<TestCase>()
        };

        var parseOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };

        await using var fs = File.OpenRead(filePath);
        using var reader = new StreamReader(fs);

        var lineNumber = 0;
        var firstContentLineSeen = false;

        while (await reader.ReadLineAsync() is { } line)
        {
            lineNumber++;
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // First non-comment, non-empty line: check for optional _suiteVars sentinel.
            // A suiteVars block has a "_suiteVars" property and no "id" property.
            if (!firstContentLineSeen)
            {
                firstContentLineSeen = true;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("_suiteVars", out var varsElement)
                        && !root.TryGetProperty("id", out _)
                        && varsElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in varsElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                suite.SuiteVars[prop.Name] = prop.Value.GetString()!;
                        }
                        // Consumed as config — do not add to Cases.
                        continue;
                    }
                }
                catch (JsonException)
                {
                    // Not valid JSON or not an object — fall through to normal case parsing.
                }
            }

            try
            {
                var testCase = JsonSerializer.Deserialize<TestCase>(line, parseOptions);

                if (testCase != null)
                {
                    testCase.Id = string.IsNullOrWhiteSpace(testCase.Id)
                        ? $"{suite.Name}_{lineNumber}"
                        : testCase.Id;
                    suite.Cases.Add(testCase);
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[PARSE ERROR] {filePath}:{lineNumber} -> {ex.Message}");
            }
        }

        return suite;
    }
}
