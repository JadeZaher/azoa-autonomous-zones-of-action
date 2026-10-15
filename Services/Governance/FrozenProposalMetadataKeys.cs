// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Governance;

/// <summary>
/// The frozen part of a governance proposal that does NOT live in metadata: the question
/// itself. Frozen from the first vote of record onward, exactly as
/// <see cref="ConsumerGovernanceHolonTypes.FrozenProposalMetadataKeys"/> is.
/// </summary>
/// <remarks>
/// <para>
/// <c>Holon.Name</c> and <c>Holon.Description</c> are COLUMNS, not metadata keys, so the
/// metadata freeze set could never reach them. That is why the question stayed editable
/// after the ballots were in, and why "Should we fund the roof repair?" could become
/// "Should we dissolve the co-op?" over a count nobody re-took. Moving the question is
/// the decisive attack on a vote of record; see <c>Services/Governance/AGENTS.md</c>
/// §proposal-freeze.
/// </para>
/// <para>
/// The typo argument that once kept these two editable is real but smaller than the
/// attack: the author owns the wording outright until somebody votes, and after that a
/// correction is a new proposal rather than a silent edit to a decided one.
/// </para>
/// </remarks>
public static class FrozenProposalQuestion
{
    /// <summary>The proposal title, as the holon API spells it.</summary>
    public const string NameField = "name";

    /// <summary>The proposal body, as the holon API spells it.</summary>
    public const string DescriptionField = "description";

    /// <summary>
    /// Names the question fields an incoming write would CHANGE, or null when it changes
    /// none of them. Compares values, so re-sending the recorded wording stays idempotent
    /// and a retry is never mistaken for a rewrite; a null incoming value means the field
    /// is untouched, which is the manager's own merge rule.
    /// </summary>
    public static string? DescribeQuestionRewrite(
        string? recordedName,
        string? recordedDescription,
        string? incomingName,
        string? incomingDescription)
    {
        var changed = new List<string>(2);

        if (incomingName is not null
            && !string.Equals(incomingName, recordedName, StringComparison.Ordinal))
            changed.Add(NameField);

        if (incomingDescription is not null
            && !string.Equals(incomingDescription, recordedDescription, StringComparison.Ordinal))
            changed.Add(DescriptionField);

        // Sorted so the refusal a caller sees does not depend on argument order.
        return changed.Count == 0
            ? null
            : string.Join(", ", changed.OrderBy(field => field, StringComparer.Ordinal));
    }
}
