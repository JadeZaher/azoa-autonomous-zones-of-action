// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Kyc;

/// <summary>Server-side participant eligibility for actions that can move or create value.</summary>
public enum ValueAccessState
{
    VerificationRequired,
    Ready,
}

/// <summary>Non-sensitive result of the single participant-readiness decision.</summary>
public sealed record ValueAccessDecision(ValueAccessState State)
{
    public bool AllowsValueActions => State == ValueAccessState.Ready;
}
