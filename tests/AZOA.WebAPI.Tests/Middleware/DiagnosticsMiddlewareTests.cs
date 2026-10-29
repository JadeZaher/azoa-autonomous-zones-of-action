using AZOA.WebAPI.Core.Diagnostics;
using AZOA.WebAPI.Middleware;
using AZOA.WebAPI.Models.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace AZOA.WebAPI.Tests.Middleware;

public sealed class DiagnosticsMiddlewareTests
{
    [Fact]
    public async Task DebugExceptionMiddleware_LogsUnhandledExceptionOnceAtCritical()
    {
        var logger = new RecordingLogger<DebugExceptionMiddleware>();
        var middleware = new DebugExceptionMiddleware(
            _ => throw new InvalidOperationException("unexpected"),
            logger);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        using var activity = new Activity("request").Start();

        await middleware.InvokeAsync(context);

        logger.Records.Should().ContainSingle();
        logger.Records[0].Level.Should().Be(LogLevel.Critical);
        logger.Records[0].Exception.Should().BeOfType<InvalidOperationException>();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.Events.Should().ContainSingle();
        var exceptionEvent = activity.Events.Single();
        exceptionEvent.Name.Should().Be("exception");
        exceptionEvent.Tags.Should().Contain(tag =>
            tag.Key == "exception.type"
            && Equals(tag.Value, typeof(InvalidOperationException).FullName));
        exceptionEvent.Tags.Should().NotContain(tag => tag.Key == "exception.message");
    }

    [Fact]
    public async Task DebugExceptionMiddleware_SuppressedEndpoint_RedactsEvenWhenDebugIsEnabled()
    {
        var wasEnabled = AZOAResultDebug.Enabled;
        AZOAResultDebug.Enabled = true;
        try
        {
            var logger = new RecordingLogger<DebugExceptionMiddleware>();
            var middleware = new DebugExceptionMiddleware(
                _ => throw new InvalidOperationException("sensitive-infrastructure-detail"),
                logger);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new SuppressDebugExceptionDetailsAttribute()),
                "public-redacted"));

            await middleware.InvokeAsync(context);

            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain("An unexpected error occurred.");
            body.Should().NotContain("sensitive-infrastructure-detail");
            body.Should().NotContain(nameof(InvalidOperationException));
            context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
            logger.Records.Should().ContainSingle();
            logger.Records[0].Level.Should().Be(LogLevel.Critical);
            logger.Records[0].Exception.Should().BeOfType<InvalidOperationException>();
        }
        finally
        {
            AZOAResultDebug.Enabled = wasEnabled;
        }
    }

    [Fact]
    public async Task JsonlExceptionMiddleware_DoesNotInterceptOrDuplicateUnhandledException()
    {
        var logger = new RecordingLogger<JsonlExceptionMiddleware>();
        var middleware = new JsonlExceptionMiddleware(
            _ => throw new InvalidOperationException("unexpected"),
            logger);
        var context = new DefaultHttpContext();

        Func<Task> act = () => middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("unexpected");
        logger.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task JsonlExceptionMiddleware_LogsCompletedErrorResponseOnce()
    {
        var logger = new RecordingLogger<JsonlExceptionMiddleware>();
        var middleware = new JsonlExceptionMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(new DefaultHttpContext());

        logger.Records.Should().ContainSingle();
        logger.Records[0].Level.Should().Be(LogLevel.Warning);
        logger.Records[0].Exception.Should().BeNull();
    }

    [Fact]
    public void Settings_UseLoggingLogLevelAsTheOnlySeverityKnob()
    {
        var root = FindRepositoryRoot();

        AssertSeverity(root, "appsettings.json", "Information", hasJsonlSettings: false);
        AssertSeverity(root, "appsettings.Development.json", "Debug", hasJsonlSettings: true);
        AssertSeverity(root, "appsettings.Production.json", "Critical", hasJsonlSettings: true);
    }

    [Fact]
    public async Task JsonlWriter_StopAsync_DrainsAcceptedEntries()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var writer = new JsonlExceptionWriter(new JsonlExceptionLoggerOptions
            {
                Enabled = true,
                Directory = directory,
            });
            await writer.StartAsync(CancellationToken.None);

            for (var index = 0; index < 12; index++)
                writer.Enqueue(CreateEntry($"entry-{index}")).Should().BeTrue();

            await writer.StopAsync(CancellationToken.None);

            var lines = Directory.GetFiles(directory, "*.jsonl")
                .SelectMany(File.ReadAllLines)
                .ToArray();
            lines.Should().HaveCount(12);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            }
            writer.FailureCount.Should().Be(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonlWriter_OversizedEntry_RemainsBoundedValidJson()
    {
        const int limit = 256;
        var directory = CreateTempDirectory();
        try
        {
            using var writer = new JsonlExceptionWriter(new JsonlExceptionLoggerOptions
            {
                Enabled = true,
                Directory = directory,
                MaxEntrySizeBytes = limit,
            });
            await writer.StartAsync(CancellationToken.None);
            writer.Enqueue(CreateEntry(new string('x', 10_000))).Should().BeTrue();

            await writer.StopAsync(CancellationToken.None);

            var line = File.ReadAllLines(Directory.GetFiles(directory, "*.jsonl").Single()).Single();
            System.Text.Encoding.UTF8.GetByteCount(line).Should().BeLessThanOrEqualTo(limit);
            using var document = JsonDocument.Parse(line);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonlLogger_WritesPersonalAndBankSurrealParametersRedactedByDefault()
    {
        var directory = CreateTempDirectory();
        try
        {
            var options = new JsonlExceptionLoggerOptions { Enabled = true, Directory = directory };
            using var writer = new JsonlExceptionWriter(options);
            await writer.StartAsync(CancellationToken.None);
            var logger = new JsonlExceptionLogger("AZOA.WebAPI.Tests", writer, options, new LoggerExternalScopeProvider());

            // The KYC identity binding's raw parameters, a typed write of the same record, and bank fields.
            var personal = new Dictionary<string, object?>
            {
                ["_binding_first_name"] = "SENTINEL-first",
                ["_binding_last_name"] = "SENTINEL-last",
                ["_binding_birthday"] = "SENTINEL-birthday",
                ["_binding_citizenship"] = "SENTINEL-citizenship",
                ["_binding_person_key"] = "SENTINEL-person-key",
                ["_binding_global_person_key"] = "SENTINEL-global-person-key",
                ["date_of_birth"] = "SENTINEL-dob",
                ["account_number"] = "SENTINEL-account",
                ["routing_number"] = "SENTINEL-routing",
                ["id_number"] = "SENTINEL-id-number",
                ["ssn"] = "SENTINEL-ssn",
                ["_binding_now"] = "2026-10-28T00:00:00Z",
            };
            var typed = SurrealForge.Client.Query.SurrealWriter.Create(new AZOA.WebAPI.Persistence.SurrealDb.Models.KycIdentity
            {
                Id = "probe",
                TenantScope = "node",
                PersonKey = "SENTINEL-typed-person-key",
                GlobalPersonKey = "SENTINEL-typed-global-person-key",
                FirstName = "SENTINEL-typed-first",
                LastName = "SENTINEL-typed-last",
                Birthday = "SENTINEL-typed-birthday",
                Citizenship = "SENTINEL-typed-citizenship",
                ProviderKey = "identomat",
            });
            foreach (var (key, value) in typed.Params)
                personal["typed_" + key] = value;

            var fault = new InvalidOperationException("A statement failed.");
            fault.Data["SurrealStatement"] = typed.Sql;
            fault.Data["SurrealParams"] = personal;
            logger.Log(LogLevel.Critical, new EventId(1), "A KYC identity binding failed.", fault, (state, _) => state);
            await writer.StopAsync(CancellationToken.None);

            var line = File.ReadAllLines(Directory.GetFiles(directory, "*.jsonl").Single()).Single();
            line.Should().NotContain("SENTINEL", "no personal or bank value may reach the diagnostic sink");
            using var document = JsonDocument.Parse(line);
            var logged = document.RootElement.GetProperty("surrealParams");
            logged.GetProperty("_binding_first_name").GetString().Should().Be("[REDACTED]");
            logged.GetProperty("account_number").GetString().Should().Be("[REDACTED]");
            logged.GetProperty("_binding_now").GetString().Should().Be("2026-10-28T00:00:00Z", "non-personal parameters stay readable");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonlWriter_WriteFailure_IncrementsFallbackCounter()
    {
        var directory = CreateTempDirectory();
        var fileInsteadOfDirectory = Path.Combine(directory, "blocked");
        await File.WriteAllTextAsync(fileInsteadOfDirectory, "not a directory");
        try
        {
            using var writer = new JsonlExceptionWriter(new JsonlExceptionLoggerOptions
            {
                Enabled = true,
                Directory = fileInsteadOfDirectory,
            });
            await writer.StartAsync(CancellationToken.None);
            writer.Enqueue(CreateEntry("write-failure")).Should().BeTrue();

            await writer.StopAsync(CancellationToken.None);

            writer.FailureCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertSeverity(
        string root,
        string fileName,
        string expected,
        bool hasJsonlSettings)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, fileName)));
        document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel")
            .GetProperty("Default")
            .GetString()
            .Should()
            .Be(expected);

        if (!hasJsonlSettings)
            return;

        var jsonl = document.RootElement
            .GetProperty("Diagnostics")
            .GetProperty("JsonlExceptionLogger");
        jsonl.GetProperty("Enabled").GetBoolean().Should().BeTrue();
        jsonl.GetProperty("Directory").GetString().Should().NotBeNullOrWhiteSpace();
        jsonl.TryGetProperty("MinimumLevel", out _).Should().BeFalse();
    }

    private static JsonlEntry CreateEntry(string message)
        => new()
        {
            Ts = DateTime.UtcNow.ToString("O"),
            Level = "Critical",
            Category = "AZOA.WebAPI.Tests",
            Message = message,
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"azoa-jsonl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AZOA.WebAPI.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the AZOA repository root.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add(new LogRecord(logLevel, exception, formatter(state, exception)));
    }

    private sealed record LogRecord(LogLevel Level, Exception? Exception, string Message);

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
