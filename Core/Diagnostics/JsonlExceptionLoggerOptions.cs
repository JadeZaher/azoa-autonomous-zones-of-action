using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>Bound from the <c>Diagnostics:JsonlExceptionLogger</c> config section.</summary>
public sealed class JsonlExceptionLoggerOptions
{
    /// <summary>Enables or disables the logger entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory where JSONL files are written (relative to content root or absolute).</summary>
    public string Directory { get; set; } = "logs/exceptions";

    /// <summary>Keys whose values are replaced by <c>[REDACTED]</c> (substring match, case-insensitive).</summary>
    public List<string> RedactionKeys { get; set; } =
    [
        "password",
        "passwordhash",
        "apikey",
        "x-api-key",
        "authorization",
        "token",
        "secret",
        "mnemonic",
        "privatekey",
    ];

    /// <summary>Truncate serialized entry to this byte limit before writing.</summary>
    public int MaxEntrySizeBytes { get; set; } = 32_768;

    /// <summary>Minimum log level that triggers a JSONL row.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
}
