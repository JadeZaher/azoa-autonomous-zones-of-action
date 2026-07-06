// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AZOA.WebAPI.Services.Backfill;

/// <summary>
/// Convenience base for an <see cref="IBackfill"/>: derives a stable
/// <see cref="Checksum"/> from a body-source string and defaults
/// <see cref="Order"/> to 0. Concrete units override <see cref="ApplyAsync"/>.
/// </summary>
/// <remarks>See <c>Services/Backfill/AGENTS.md</c>.</remarks>
public abstract class BackfillBase : IBackfill
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual int Order => 0;

    /// <summary>SHA-256 of <see cref="ChecksumSource"/> (hex). Advisory drift signal only.</summary>
    public string Checksum => Sha256Hex(ChecksumSource);

    /// <summary>String the checksum is derived from; override to include the rewrite's logical body. Defaults to <see cref="Id"/>.</summary>
    protected virtual string ChecksumSource => Id;

    public abstract Task<BackfillResult> ApplyAsync(BackfillContext context, CancellationToken ct = default);

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
