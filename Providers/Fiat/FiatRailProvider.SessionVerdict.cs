// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Fiat;

public sealed partial class FiatRailProvider
{
    /// <summary>What a fresh Stripe read proves about one attached Checkout session.</summary>
    private enum CheckoutSessionVerdict
    {
        /// <summary>Paid, or complete and still clearing: something was paid; never release the key.</summary>
        PaidOrClearing,

        /// <summary>Expired unpaid: nothing was or can be paid.</summary>
        Dead,

        /// <summary>Open: payable until expired at Stripe.</summary>
        Payable,

        /// <summary>Unreadable, unrecognised, or still open after an expire attempt: refuse to decide.</summary>
        Unknown,
    }

    /// <summary>A settled verdict and the Stripe read it rests on (null when Stripe could not be read).</summary>
    private readonly record struct SettledCheckoutSession(
        CheckoutSessionVerdict Verdict, StripeCheckoutSession? Provider, string? Message = null);

    /// <summary>
    /// The one classifier for void and same-key resume; "complete" alone is never treated as dead.
    /// See <c>Providers/Fiat/AGENTS.md</c> §stripe-session-verdict.
    /// </summary>
    private static CheckoutSessionVerdict JudgeCheckoutSession(StripeCheckoutSession provider)
    {
        if (IsPaid(provider) || string.Equals(provider.Status, "complete", StringComparison.OrdinalIgnoreCase))
            return CheckoutSessionVerdict.PaidOrClearing;
        if (string.Equals(provider.Status, "expired", StringComparison.OrdinalIgnoreCase))
            return CheckoutSessionVerdict.Dead;
        return string.Equals(provider.Status, "open", StringComparison.OrdinalIgnoreCase)
            ? CheckoutSessionVerdict.Payable
            : CheckoutSessionVerdict.Unknown;
    }

    /// <summary>
    /// Settles an attached session to <c>Dead</c> or <c>PaidOrClearing</c> from a fresh read, expiring an
    /// open page AT STRIPE first; never returns <c>Payable</c>. See <c>Providers/Fiat/AGENTS.md</c> §stripe-session-verdict.
    /// </summary>
    private async Task<SettledCheckoutSession> SettleCheckoutSessionAsync(
        HostedSession session, StripeCheckoutSession fresh, CancellationToken ct)
    {
        var verdict = JudgeCheckoutSession(fresh);
        if (verdict != CheckoutSessionVerdict.Payable)
            return new(verdict, fresh);

        var expired = await _gateway.ExpireCheckoutSessionAsync(
            _options.CredentialsFor(_environment), session.ProviderSessionId!,
            FiatCheckoutVoid.ExpireIdempotencyKey(session.Id, session.ProviderSessionId!), ct);
        var after = expired.IsError ? null : expired.Result;

        // A refused or lost expire (the payer finished concurrently, Stripe busy, connection reset) is
        // settled by a fresh read, never assumed.
        if (after is null || JudgeCheckoutSession(after) is not (CheckoutSessionVerdict.Dead or CheckoutSessionVerdict.PaidOrClearing))
        {
            var reread = await ReadProviderSessionAsync(session, ct);
            if (reread.IsError || reread.Result is null)
                return new(CheckoutSessionVerdict.Unknown, null, reread.Message);
            after = reread.Result;
        }

        var settled = JudgeCheckoutSession(after);
        return new(settled is CheckoutSessionVerdict.Dead or CheckoutSessionVerdict.PaidOrClearing
            ? settled
            : CheckoutSessionVerdict.Unknown, after);
    }
}
