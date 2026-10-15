// SPDX-License-Identifier: UNLICENSED

using System.Security.Cryptography;
using System.Text;

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// The ONE canonical preimage + digest for a governance tally (D20). Everything
/// that publishes, verifies, or anchors a tally hash calls this — a second
/// serialization is a second answer.
/// </summary>
/// <remarks>
/// <para>
/// The preimage contains the proposal id, the close time, the total, and the
/// per-option counts sorted ascending — and NO VOTER IDENTITIES. That is the whole
/// reason a tally is computed off-chain and only the digest may be anchored: putting
/// ballots on chain would make every member's choice permanently public, which is
/// usually the opposite of what a co-op wants (D20).
/// </para>
/// <para>
/// Each option is written LENGTH-PREFIXED (<c>{charCount}:{option}={count}</c>) so an
/// option containing the separator characters cannot forge a different tally with the
/// same preimage — the classic canonicalization collision.
/// </para>
/// </remarks>
public static class GovernanceTallyHash
{
    /// <summary>Domain-separation tag. Bump the version if the preimage layout ever changes.</summary>
    public const string Domain = "AZOA.Governance.Tally.v1";

    /// <summary>
    /// Order-stable serialization of (proposalId, closesAt, totalVotes, sorted option
    /// counts). Options are ordered with <see cref="StringComparer.Ordinal"/> so the
    /// bytes do not move with the caller's culture or the dictionary's iteration order.
    /// </summary>
    public static string Preimage(
        Guid proposalId,
        string? closesAt,
        int totalVotes,
        IReadOnlyDictionary<string, int> countsByOption)
    {
        ArgumentNullException.ThrowIfNull(countsByOption);

        var builder = new StringBuilder()
            .Append(Domain).Append('\n')
            .Append(proposalId.ToString("D")).Append('\n')
            .Append(closesAt ?? string.Empty).Append('\n')
            .Append(totalVotes.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');

        foreach (var option in countsByOption.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder
                .Append(option.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(option)
                .Append('=')
                .Append(countsByOption[option].ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Lower-case hex SHA-256 of <see cref="Preimage"/>. This is the value a tenant may anchor on chain.</summary>
    public static string Compute(
        Guid proposalId,
        string? closesAt,
        int totalVotes,
        IReadOnlyDictionary<string, int> countsByOption)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Preimage(proposalId, closesAt, totalVotes, countsByOption));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
