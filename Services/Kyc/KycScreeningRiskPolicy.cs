// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// Derives AZOA's own AML risk verdict. The provider publishes NO approved/rejected
/// enum on the screening surface, so hit / no-hit / manual-review is entirely our
/// decision, taken from <c>score</c> plus the OPEN <c>topics</c> string set. See
/// <c>Services/Kyc/AGENTS.md</c> §aml-screening.
/// </summary>
public interface IKycScreeningRiskPolicy
{
    /// <summary>Version stamp recorded alongside every decision it produces.</summary>
    string PolicyVersion { get; }

    /// <summary>Threshold sent as the server-side filter; persisted with each result.</summary>
    int ScoreThreshold { get; }

    /// <summary>
    /// Classifies a result set returned at <see cref="ScoreThreshold"/>.
    /// <paramref name="resultsTruncated"/> says the hit list may be incomplete; an
    /// incomplete list can never be evidence of absence, so it can never read NO_HIT.
    /// </summary>
    KycScreeningAssessment Assess(
        IReadOnlyList<IdentomatScreeningHit> hits,
        bool resultsTruncated = false);
}

/// <summary>
/// The one AML clearance predicate, and the recourse window that opens when it is
/// not met. It lives beside the risk policy because "cleared" and "what happens when
/// not cleared" are a single policy: a value gate and a status projection that answer
/// them differently strand a subject who is told they are ready and denied anyway.
/// See <c>Services/Kyc/AGENTS.md</c> §aml-screening.
/// </summary>
public static class KycAmlClearance
{
    /// <summary>Node-operator SLA, in hours, for deciding a flagged screening.</summary>
    public const string ManualReviewSlaHoursConfigurationPath = "Kyc:ManualReviewSlaHours";

    public const int DefaultManualReviewSlaHours = 72;
    public const int MinimumManualReviewSlaHours = 1;
    public const int MaximumManualReviewSlaHours = 720;

    /// <summary>
    /// Wording a subject or tenant may see. It names no list, score, topic or vendor:
    /// an adverse AML finding is not something to tip a subject off about, and a
    /// false-positive name match must not read as an accusation either.
    /// </summary>
    public const string UnderReviewReason =
        "Identity verification is complete and an additional compliance check is under review. "
        + "No further action is needed; support will confirm the outcome.";

    /// <summary>
    /// Exactly the rule the value gates apply: only a NO_HIT verdict over a complete
    /// hit list clears. A missing row is an unanswered question, never a clean one.
    /// </summary>
    public static bool IsClear(KycScreeningResult? result)
        => result is { Decision: KycScreeningDecision.NO_HIT, ResultsTruncated: false };

    /// <summary>True when a human still owes this subject a decision.</summary>
    public static bool RequiresManualReview(KycScreeningResult? result) => !IsClear(result);

    /// <summary>Where a subject under review reaches a human; node-operator config.</summary>
    public const string ManualReviewContactConfigurationPath = "Kyc:ManualReviewContact";

    /// <summary>Longest contact string this node will project to a tenant.</summary>
    public const int MaximumManualReviewContactLength = 200;

    /// <summary>
    /// The operator's route to a human, sanitised for projection, or null when the
    /// operator set none. Whitespace-only, control-bearing and over-long values
    /// resolve to null rather than to a broken route: a contact a subject cannot use
    /// is worse than an absent one, because it reads as recourse that exists.
    /// </summary>
    public static string? ResolveManualReviewContact(IConfiguration? configuration)
    {
        var contact = configuration?[ManualReviewContactConfigurationPath]?.Trim();
        if (string.IsNullOrEmpty(contact) || contact.Length > MaximumManualReviewContactLength)
            return null;

        return contact.Any(char.IsControl) ? null : contact;
    }

    /// <summary>
    /// The wording a subject sees, carrying the operator's contact when one is set.
    /// The contact is the whole point of this recourse: without it "support will
    /// confirm the outcome" names nobody, and a legitimate sponsor caught by a
    /// sanctions-name collision has no one to ask.
    /// </summary>
    public static string ResolveUnderReviewReason(IConfiguration? configuration)
    {
        var contact = ResolveManualReviewContact(configuration);
        return contact is null ? UnderReviewReason : $"{UnderReviewReason} Contact {contact}.";
    }

    /// <summary>
    /// True once the operator's own SLA for an open review has elapsed. A null due
    /// date is NOT overdue: it means nothing has been screened yet, which is a
    /// different failure and carries its own alarm.
    /// </summary>
    public static bool IsOverdue(DateTimeOffset? reviewDueAt, DateTimeOffset asOf)
        => reviewDueAt is not null && asOf > reviewDueAt.Value;

    /// <summary>Operator-configured review SLA, clamped so a typo cannot remove it.</summary>
    public static TimeSpan ResolveManualReviewSla(IConfiguration? configuration)
    {
        var hours = configuration?.GetValue(
            ManualReviewSlaHoursConfigurationPath,
            DefaultManualReviewSlaHours) ?? DefaultManualReviewSlaHours;

        return TimeSpan.FromHours(
            Math.Clamp(hours, MinimumManualReviewSlaHours, MaximumManualReviewSlaHours));
    }

    /// <summary>
    /// When the operator's decision is due. Null when nothing is under review, and
    /// null when nothing has been screened yet — the SLA clock starts at the screen.
    /// </summary>
    public static DateTimeOffset? ReviewDueAt(KycScreeningResult? result, TimeSpan sla)
        => result is null || IsClear(result) ? null : result.ScreenedAt.Add(sla);
}

/// <summary>AZOA's verdict plus the evidence that produced it.</summary>
public sealed class KycScreeningAssessment
{
    public KycScreeningDecision Decision { get; init; }
    public int TopScore { get; init; }
    public int HitCount { get; init; }

    /// <summary>Union of the hits' topics; an open string set, never an enum.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    public IReadOnlyList<string> PersonIds { get; init; } = [];

    /// <summary>Topics that triggered an automatic block, if any.</summary>
    public IReadOnlyList<string> BlockingTopicsMatched { get; init; } = [];

    /// <summary>True when the provider's hit list saturated the paged fetch.</summary>
    public bool ResultsTruncated { get; init; }
}

/// <inheritdoc />
public sealed class KycScreeningRiskPolicy : IKycScreeningRiskPolicy
{
    /// <summary>Bump when the bands below change so old rows stay interpretable.</summary>
    public const string CurrentPolicyVersion = "azoa-screening-v1";

    private readonly IdentomatScreeningSettings _settings;
    private readonly HashSet<string> _blockingTopics;

    public KycScreeningRiskPolicy(IOptions<KycSettings> options)
    {
        _settings = options.Value.Identomat.Screening;
        // Case-insensitive exact match over an OPEN set. A closed C# enum would
        // reject topics the vendor has not published, and the vendor does not
        // publish an exhaustive list.
        _blockingTopics = new HashSet<string>(
            _settings.BlockingTopics ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public string PolicyVersion => CurrentPolicyVersion;

    public int ScoreThreshold => Math.Clamp(_settings.ScoreThreshold, 1, 100);

    public KycScreeningAssessment Assess(
        IReadOnlyList<IdentomatScreeningHit> hits,
        bool resultsTruncated = false)
    {
        if (hits is null || hits.Count == 0)
        {
            return new KycScreeningAssessment
            {
                // A truncated fetch that returned nothing is not a clean subject; it
                // is an unfinished question, and NO_HIT would answer it as "clean".
                Decision = resultsTruncated
                    ? KycScreeningDecision.MANUAL_REVIEW
                    : KycScreeningDecision.NO_HIT,
                TopScore = 0,
                HitCount = 0,
                ResultsTruncated = resultsTruncated,
            };
        }

        var topics = hits
            .SelectMany(hit => hit.Topics ?? [])
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Select(topic => topic.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(topic => topic, StringComparer.Ordinal)
            .ToList();
        var personIds = hits
            .Select(hit => hit.PersonId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var blocking = topics.Where(_blockingTopics.Contains).ToList();
        var topScore = hits.Max(hit => hit.Score);
        var autoBlockScore = Math.Clamp(_settings.AutoBlockScore, 1, 100);

        // A blocking topic blocks at ANY returned score: the score measures identity
        // match confidence, not how severe the listing is.
        var decision = blocking.Count > 0 || topScore >= autoBlockScore
            ? KycScreeningDecision.HIT
            : KycScreeningDecision.MANUAL_REVIEW;

        return new KycScreeningAssessment
        {
            Decision = decision,
            TopScore = topScore,
            HitCount = hits.Count,
            Topics = topics,
            PersonIds = personIds,
            BlockingTopicsMatched = blocking,
            ResultsTruncated = resultsTruncated,
        };
    }
}
