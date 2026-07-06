using System.Text.Json;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Custody;

// ─── DI registration (orchestrator applies to Program.cs — REPORTED for reconciliation) ───
//   builder.Services.AddSingleton<IPendingRotationKeyStore, FilePendingRotationKeyStore>();
// Singleton: it is a thin stateless wrapper over a single on-disk file; no per-request
// state. Registered near the KeyRotationService registration. See Services/Custody/AGENTS.md §rotation.

/// <summary>
/// security-review HIGH-1: file-backed <see cref="IPendingRotationKeyStore"/>. The marker
/// lives OUTSIDE SurrealDB deliberately — a rotation is an operator disaster-recovery
/// primitive, and the marker must survive even a DB outage that itself might have caused
/// the rotation to be attempted. The path is config-driven
/// (<c>AZOA:Rotation:PendingKeyFilePath</c>), defaulting to
/// <c>pending-rotation.json</c> under the content root.
/// <para>
/// The file never contains raw key material: only the ISO start timestamp, an AES-GCM
/// verifier token (a sentinel encrypted under the new key), and the wallet count. Writes
/// are atomic (write-temp-then-move) so a crash mid-write can't leave a torn marker.
/// </para>
/// </summary>
public sealed class FilePendingRotationKeyStore : IPendingRotationKeyStore
{
    private const string ConfigPath = "AZOA:Rotation:PendingKeyFilePath";
    private const string DefaultFileName = "pending-rotation.json";

    private readonly string _filePath;
    private readonly ILogger<FilePendingRotationKeyStore> _logger;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public FilePendingRotationKeyStore(
        IConfiguration config,
        IHostEnvironment env,
        ILogger<FilePendingRotationKeyStore> logger)
    {
        _logger = logger;
        var configured = config.GetValue<string>(ConfigPath);
        _filePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, DefaultFileName)
            : configured;
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> WritePendingAsync(string verifierToken, int walletsInScope, CancellationToken ct = default)
    {
        var record = new PendingRotationRecord
        {
            StartedUtc = DateTime.UtcNow.ToString("O"),
            VerifierToken = verifierToken,
            WalletsInScope = walletsInScope,
        };

        await _gate.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(record);
            var tmp = _filePath + ".tmp";
            // fsync the temp file before the atomic rename so a hard power-loss in the
            // sub-second window after Move cannot lose a marker WritePendingAsync reported
            // as persisted — the marker is the only thing standing between a mid-rotation
            // crash and unrecoverable key loss, so its durability must be real, not cached.
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(bytes, ct);
                fs.Flush(flushToDisk: true);
            }
            // Atomic replace so a torn write never yields a half-marker.
            File.Move(tmp, _filePath, overwrite: true);
            _logger.LogWarning(
                "Pending-rotation marker written at {Path}: a wrapping-key rotation is in flight over {Count} wallets. " +
                "Do NOT discard the new key candidate until this marker clears.", _filePath, walletsInScope);
            return new AZOAResult<bool> { Result = true };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"Failed to persist pending-rotation marker: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
            return new AZOAResult<bool> { Result = true };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"Failed to clear pending-rotation marker: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AZOAResult<PendingRotationRecord?>> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
                return new AZOAResult<PendingRotationRecord?> { Result = null };

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var record = JsonSerializer.Deserialize<PendingRotationRecord>(json);
            return new AZOAResult<PendingRotationRecord?> { Result = record };
        }
        catch (Exception ex)
        {
            return new AZOAResult<PendingRotationRecord?>().CaptureException(ex, $"Failed to read pending-rotation marker: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
