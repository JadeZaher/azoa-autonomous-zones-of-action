using System.Diagnostics;
using OASIS.WebAPI.LiveTests.Models;
using OASIS.WebAPI.LiveTests.Parsers;
using OASIS.WebAPI.LiveTests.Reporters;

namespace OASIS.WebAPI.LiveTests;

/// <summary>
/// Orchestrates discovery, parallel execution, and reporting of live API tests.
/// </summary>
public class TestHarness
{
    private readonly HarnessConfig _config;
    private readonly HttpClient _httpClient;

    public TestHarness(HarnessConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        foreach (var (key, value) in config.DefaultHeaders)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    public async Task<TestSummary> RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"🔍 Discovering tests in: {_config.PayloadDirectory}");
        var suites = await JsonlTestParser.DiscoverAndParseAsync(_config.PayloadDirectory, _config.FilePattern);

        if (suites.Count == 0)
        {
            Console.WriteLine("⚠️ No test suites found.");
            return new TestSummary { BaseUrl = _config.BaseUrl, TotalSuites = 0 };
        }

        Console.WriteLine($"🚀 Found {suites.Count} suite(s) with {suites.Sum(s => s.Cases.Count)} total case(s).");
        Console.WriteLine($"⚡ Running up to {_config.MaxParallelSuites} suites in parallel...\n");

        var summary = new TestSummary
        {
            StartedAt = DateTime.UtcNow,
            BaseUrl = _config.BaseUrl,
            TotalSuites = suites.Count,
            TotalCases = suites.Sum(s => s.Cases.Count)
        };

        var semaphore = new SemaphoreSlim(_config.MaxParallelSuites);
        var suiteTasks = suites.Select(suite => RunSuiteAsync(suite, semaphore, ct)).ToList();
        var suiteResults = await Task.WhenAll(suiteTasks);

        summary.SuiteResults.AddRange(suiteResults);
        summary.Passed = summary.SuiteResults.Sum(r => r.Passed);
        summary.Failed = summary.SuiteResults.Sum(r => r.Failed);
        summary.Skipped = summary.SuiteResults.Sum(r => r.Skipped);
        summary.TotalDurationMs = summary.SuiteResults.Sum(r => r.DurationMs);
        summary.CompletedAt = DateTime.UtcNow;

        // Write report
        var reporter = new MarkdownReporter(
            includeResponseBodies: _config.IncludeResponseBodies,
            truncateResponseBodyAt: _config.TruncateResponseBodyAt);

        await File.WriteAllTextAsync(_config.ResultsPath, reporter.Render(summary), ct);
        Console.WriteLine($"\n📝 Results written to: {_config.ResultsPath}");
        Console.WriteLine($"✅ Passed: {summary.Passed}  ❌ Failed: {summary.Failed}  ⏭️ Skipped: {summary.Skipped}");

        return summary;
    }

    private async Task<TestSuiteResult> RunSuiteAsync(TestSuite suite, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var client = new HttpTestClient(_httpClient, _config.IncludeResponseBodies, _config.TruncateResponseBodyAt);
            var context = new Dictionary<string, string>();
            var results = new List<TestResult>();

            Console.WriteLine($"[{suite.Name}] Starting {suite.Cases.Count} case(s)...");

            foreach (var testCase in suite.Cases)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (testCase.Skip)
                {
                    results.Add(new TestResult
                    {
                        Suite = suite.Name,
                        TestId = testCase.Id,
                        Description = testCase.Description,
                        Passed = true,
                        Error = "SKIPPED"
                    });
                    Console.WriteLine($"  ⏭️  {testCase.Id}: SKIPPED");
                    continue;
                }

                var result = await client.ExecuteAsync(testCase, suite.Name, context);

                // Merge extracted values into shared context for chaining
                foreach (var (key, value) in result.ExtractedValues)
                {
                    context[key] = value;
                }

                results.Add(result);

                var icon = result.Passed ? "✅" : "❌";
                Console.WriteLine($"  {icon} {testCase.Id}: {result.StatusCode} in {result.DurationMs}ms{(result.Error != null ? $" | {result.Error}" : "")}");
            }

            sw.Stop();

            return new TestSuiteResult
            {
                SuiteName = suite.Name,
                Total = results.Count,
                Passed = results.Count(r => r.Passed && r.Error != "SKIPPED"),
                Failed = results.Count(r => !r.Passed && r.Error != "SKIPPED"),
                Skipped = results.Count(r => r.Error == "SKIPPED"),
                DurationMs = sw.ElapsedMilliseconds,
                Results = results
            };
        }
        finally
        {
            semaphore.Release();
        }
    }
}

public class HarnessConfig
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxParallelSuites { get; set; } = 4;
    public Dictionary<string, string> DefaultHeaders { get; set; } = new();
    public string PayloadDirectory { get; set; } = "live-tests";
    public string FilePattern { get; set; } = "*.jsonl";
    public string ResultsPath { get; set; } = "live-test-results.md";
    public bool IncludeResponseBodies { get; set; } = true;
    public int TruncateResponseBodyAt { get; set; } = 2000;
}
