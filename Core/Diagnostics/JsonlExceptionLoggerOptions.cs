using System.Collections.Generic;

namespace AZOA.WebAPI.Core.Diagnostics;

/// <summary>Bound from the <c>Diagnostics:JsonlExceptionLogger</c> config section.</summary>
public sealed class JsonlExceptionLoggerOptions
{
    private int _maxEntrySizeBytes = 32_768;

    /// <summary>Enables or disables the logger entirely.</summary>
    public bool Enabled { get; set; }

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

    /// <summary>Truncate serialized entries to this byte limit (effective minimum: two bytes).</summary>
    public int MaxEntrySizeBytes
    {
        get => _maxEntrySizeBytes;
        set => _maxEntrySizeBytes = value < 2 ? 2 : value;
    }
}
