using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AZOA.WebAPI.Core.Diagnostics;

namespace AZOA.WebAPI.Extensions;

/// <summary>
/// Registers the optional JSONL diagnostic logger subsystem.
/// </summary>
public static class DiagnosticsExtensions
{
    /// <summary>
    /// Binds <c>Diagnostics:JsonlExceptionLogger</c> config, registers
    /// <see cref="JsonlExceptionWriter"/> as a singleton + <see cref="IHostedService"/>,
    /// and wires the <see cref="JsonlExceptionLoggerProvider"/> into the logging pipeline.
    /// No-op unless explicitly enabled for the current environment.
    /// </summary>
    public static WebApplicationBuilder AddJsonlExceptionLogging(this WebApplicationBuilder builder)
    {
        var options = new JsonlExceptionLoggerOptions();
        builder.Configuration
            .GetSection("Diagnostics:JsonlExceptionLogger")
            .Bind(options);

        if (!options.Enabled)
            return builder;

        var writer = new JsonlExceptionWriter(options);

        builder.Services.AddSingleton(writer);
        builder.Services.AddSingleton<IHostedService>(writer);

        builder.Logging.AddProvider(new JsonlExceptionLoggerProvider(writer, options));

        return builder;
    }
}
