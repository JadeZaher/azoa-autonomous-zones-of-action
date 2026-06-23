using System.Reflection;
using Microsoft.Extensions.Configuration;
using AZOA.WebAPI.LiveTests;

Console.WriteLine("═══════════════════════════════════════════════════════════");
Console.WriteLine("  AZOA WebAPI Live Test Harness");
Console.WriteLine("═══════════════════════════════════════════════════════════\n");

// Build configuration from appsettings + environment variables + CLI args
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".")
    .AddJsonFile("appsettings.LiveTests.json", optional: false)
    .AddEnvironmentVariables("AZOA_TEST_")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        ["--url"] = "BaseUrl",
        ["-u"] = "BaseUrl",
        ["--parallel"] = "MaxParallelSuites",
        ["-p"] = "MaxParallelSuites",
        ["--output"] = "Output:ResultsPath",
        ["-o"] = "Output:ResultsPath",
        ["--dir"] = "TestDiscovery:PayloadDirectory",
        ["-d"] = "TestDiscovery:PayloadDirectory"
    });

var configuration = configBuilder.Build();

var harnessConfig = new HarnessConfig
{
    BaseUrl = configuration["BaseUrl"] ?? "http://localhost:5000",
    TimeoutSeconds = int.TryParse(configuration["TimeoutSeconds"], out var to) ? to : 30,
    MaxParallelSuites = int.TryParse(configuration["MaxParallelSuites"], out var mp) ? mp : 4,
    PayloadDirectory = configuration["TestDiscovery:PayloadDirectory"] ?? "live-tests",
    FilePattern = configuration["TestDiscovery:FilePattern"] ?? "*.jsonl",
    ResultsPath = configuration["Output:ResultsPath"] ?? "../live-test-results.md",
    IncludeResponseBodies = bool.TryParse(configuration["Output:IncludeResponseBodies"], out var irb) ? irb : true,
    TruncateResponseBodyAt = int.TryParse(configuration["Output:TruncateResponseBodyAt"], out var tr) ? tr : 2000
};

// Load default headers from config section if present
var headersSection = configuration.GetSection("DefaultHeaders");
foreach (var child in headersSection.GetChildren())
{
    harnessConfig.DefaultHeaders[child.Key] = child.Value ?? "";
}

Console.WriteLine($"Configuration:");
Console.WriteLine($"  BaseUrl:    {harnessConfig.BaseUrl}");
Console.WriteLine($"  Parallel:   {harnessConfig.MaxParallelSuites}");
Console.WriteLine($"  PayloadDir: {harnessConfig.PayloadDirectory}");
Console.WriteLine($"  Output:     {harnessConfig.ResultsPath}\n");

var harness = new TestHarness(harnessConfig);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n🛑 Cancellation requested...");
};

var summary = await harness.RunAsync(cts.Token);

// Exit with non-zero if any failures
if (summary.Failed > 0)
{
    Console.WriteLine($"\n🔴 {summary.Failed} test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine("\n🟢 All tests passed.");
