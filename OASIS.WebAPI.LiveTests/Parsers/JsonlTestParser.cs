using System.Text.Json;
using OASIS.WebAPI.LiveTests.Models;

namespace OASIS.WebAPI.LiveTests.Parsers;

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

        await using var fs = File.OpenRead(filePath);
        using var reader = new StreamReader(fs);

        var lineNumber = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            lineNumber++;
            line = line.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            try
            {
                var testCase = JsonSerializer.Deserialize<TestCase>(line, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                });

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
