// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>How long an exchange group stays valid, and how long this node waits on it.</summary>
/// <remarks>
/// Every accessor below NORMALISES rather than trusting the bound value. <c>.Get&lt;T&gt;()</c>
/// replaces a C# default outright, so an environment variable set to the empty string binds
/// zero — and a zero validity window would expire every group before it could be mined. The
/// working value is resolved in code, not left to the shape a deploy produces most easily.
/// </remarks>
public sealed class AlgorandExchangeGroupSubmitterOptions
{
    /// <summary>Configuration section. Its own, deliberately not a widening of <c>Exchange</c>.</summary>
    public const string SectionName = "Exchange:AlgorandRail";

    /// <summary>Rounds a submitted group stays valid for; past it the group can never commit.</summary>
    public int ValidityWindowRounds { get; set; } = 25;

    /// <summary>How many times a submitted group is read back before its fate is left to reconciliation.</summary>
    public int ConfirmationPollAttempts { get; set; } = 30;

    /// <summary>Milliseconds between those reads.</summary>
    public int ConfirmationPollDelayMilliseconds { get; set; } = 1000;

    /// <summary>Floor for the per-transaction fee, in microAlgos, when algod suggests less.</summary>
    public long MinimumFeeMicroAlgos { get; set; } = 1000;

    /// <summary>Validity window actually used: at least one round, at most a thousand.</summary>
    public ulong EffectiveValidityWindowRounds =>
        (ulong)Math.Clamp(ValidityWindowRounds <= 0 ? 25 : ValidityWindowRounds, 1, 1000);

    /// <summary>Poll count actually used: at least one read, at most three hundred.</summary>
    public int EffectiveConfirmationPollAttempts =>
        Math.Clamp(ConfirmationPollAttempts <= 0 ? 30 : ConfirmationPollAttempts, 1, 300);

    /// <summary>Poll delay actually used, in milliseconds.</summary>
    public int EffectiveConfirmationPollDelayMilliseconds =>
        Math.Clamp(ConfirmationPollDelayMilliseconds <= 0 ? 1000 : ConfirmationPollDelayMilliseconds, 100, 10_000);

    /// <summary>Per-transaction fee floor actually used, in microAlgos.</summary>
    public ulong EffectiveMinimumFeeMicroAlgos =>
        (ulong)Math.Clamp(MinimumFeeMicroAlgos <= 0 ? 1000 : MinimumFeeMicroAlgos, 1000, 1_000_000);
}

/// <summary>
/// Settles an exchange group on Algorand as ONE atomic group: two assets, two signers, and the
/// chain fee pooled onto the reserve.
/// </summary>
/// <remarks>
/// <para><b>Why this is not <c>IAtomicTransferGroupModule</c>.</b> That module carries the node-fee
/// split — one asset, one source, one signing context — and its equality checks are what make a
/// fee split safe. An exchange group is the opposite shape, so it is assembled here against
/// <see cref="MultiSignerAtomicGroupRequest"/> and signed through the three custody doors the
/// three signers actually have.</para>
/// <para><b>It never falls back to sequential transfers.</b> Either every leg is signed and the
/// whole group is broadcast, or nothing is broadcast at all. Two separate transfers are exactly
/// the funds-taken / units-undelivered failure atomicity exists to prevent.</para>
/// <para><b>Fee pooling.</b> A sponsor's custodial account holds no spendable ALGO, and neither
/// does a project treasury — its key is derived to hold the platform unit, not to pay fees. So
/// every leg NOT sent from the reserve carries a zero fee and the reserve's own transaction
/// carries the whole group's fee. That is a superset of
/// <see cref="ExchangeGroupPlan.UserSignedLegCount"/>, and it is checked as such before
/// anything is signed. When no leg is sent from the reserve at all (a held buy, or a sell paid
/// out of a treasury), a zero-value self-payment from the reserve is appended purely to carry
/// that pooled fee.</para>
/// <para><b>Three outcomes, and only one of them unwinds anything.</b> Confirmed moved value.
/// Dropped means the validity window closed with every read succeeding and no confirmation — the
/// group can never commit, so the manager hands every commitment back. Accepted means this node
/// does not know, and nothing may be recorded or unwound on a guess.</para>
/// </remarks>
public sealed class AlgorandExchangeGroupSubmitter : IExchangeGroupSubmitter
{
    /// <summary>The one rail this submitter settles on.</summary>
    public const string SupportedChainType = "Algorand";

    /// <summary>Named <c>IHttpClientFactory</c> client for the algod calls this submitter makes.</summary>
    public const string AlgodHttpClientName = "azoa.exchange.algorand";

    /// <summary>Refusal a consumer sees when the group could not be assembled, signed or broadcast.</summary>
    public const string UnavailableMessage =
        "This node could not settle the exchange right now. Nothing was moved.";

    /// <summary>Refusal a consumer sees when the rail took the group and it then expired unmined.</summary>
    public const string DroppedMessage =
        "This exchange did not settle in time and has been reversed. Nothing was moved.";

    private readonly IBlockchainProviderFactory _providers;
    private readonly IKeyCustodyService _custody;
    private readonly IPlatformReserve _reserve;
    private readonly IProjectTreasury _treasury;
    private readonly ITransactionSignerFactory _signers;
    private readonly IHttpClientFactory _httpClients;
    private readonly BlockchainConfigurationManager _chainConfiguration;
    private readonly AlgorandExchangeGroupSubmitterOptions _options;
    private readonly ILogger<AlgorandExchangeGroupSubmitter>? _logger;

    // The interface forbids resending a group under a key it already sent. The DURABLE anchor
    // for that is the exchange idempotency claim the manager wins before it ever calls here;
    // this set closes the remaining in-request window, where a retry could reach one resolved
    // submitter twice.
    private readonly HashSet<string> _submittedKeys = new(StringComparer.Ordinal);

    public AlgorandExchangeGroupSubmitter(
        IBlockchainProviderFactory providers,
        IKeyCustodyService custody,
        IPlatformReserve reserve,
        IProjectTreasury treasury,
        ITransactionSignerFactory signers,
        IHttpClientFactory httpClients,
        IConfiguration configuration,
        AlgorandExchangeGroupSubmitterOptions? options = null,
        ILogger<AlgorandExchangeGroupSubmitter>? logger = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _custody = custody ?? throw new ArgumentNullException(nameof(custody));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _signers = signers ?? throw new ArgumentNullException(nameof(signers));
        _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
        ArgumentNullException.ThrowIfNull(configuration);
        _chainConfiguration = new BlockchainConfigurationManager(configuration);
        _options = options ?? new AlgorandExchangeGroupSubmitterOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanSubmit(string chainType) =>
        !string.IsNullOrWhiteSpace(chainType)
        && string.Equals(chainType.Trim(), SupportedChainType, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<AZOAResult<ExchangeGroupSubmission>> SubmitAsync(
        ExchangeGroupPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        if (!CanSubmit(plan.ChainType))
        {
            return Refuse(
                $"This submitter settles {SupportedChainType} groups only; the plan names '{plan.ChainType}'.");
        }

        if (_submittedKeys.Contains(plan.IdempotencyKey))
        {
            return Refuse(
                "This exchange group has already been submitted under its own key; refusing to send it twice.");
        }

        var request = BuildRequest(plan);
        if (request.IsError || request.Result is null)
            return Refuse(request.Message);

        // Pooling must cover at least every leg a PERSON signs, because those are the accounts
        // that provably hold no spendable native units. Covering more is fine; covering fewer
        // would put the chain fee back on a sponsor.
        if (request.Result.PooledFeeLegCount < plan.UserSignedLegCount)
        {
            return Refuse(
                "The assembled group pools fewer fees than the plan has person-signed legs; refusing "
                + "to submit a group a sponsor would be asked to pay for.");
        }

        using var algod = CreateAlgodClient(plan);
        if (algod is null)
            return Refuse($"No algod endpoint is configured for {plan.ChainType}/{plan.Network}.");

        var suggested = await ReadSuggestedParametersAsync(algod, ct);
        if (suggested.IsError || suggested.Result is null)
            return Refuse(suggested.Message);

        var assembled = AssembleGroup(plan, request.Result, suggested.Result);
        if (assembled.IsError || assembled.Result is null)
            return Refuse(assembled.Message);

        var group = assembled.Result;
        var signed = await SignGroupAsync(plan, group, ct);
        if (signed.IsError || signed.Result is null)
            return Refuse(signed.Message);

        // The one irreversible step. Everything above can still refuse; nothing below can undo —
        // so the key is recorded HERE, and a group refused before this line stays retryable.
        _submittedKeys.Add(plan.IdempotencyKey);
        var broadcast = await BroadcastAsync(algod, signed.Result, ct);
        if (broadcast.IsError)
            return Refuse(broadcast.Message);

        var outcome = await ObserveAsync(algod, group, ct);
        var submission = new ExchangeGroupSubmission(
            group.ChainGroupId,
            group.PlanLegTransactionIds,
            outcome == ExchangeGroupOutcome.Confirmed,
            outcome);

        if (outcome == ExchangeGroupOutcome.DroppedAfterAcceptance)
        {
            _logger?.LogWarning(
                "Exchange group {GroupId} expired unmined on {Chain}/{Network}; the exchange is being unwound.",
                group.ChainGroupId, plan.ChainType, plan.Network);
            return AZOAResult<ExchangeGroupSubmission>.Success(submission, DroppedMessage);
        }

        return AZOAResult<ExchangeGroupSubmission>.Success(
            submission,
            outcome == ExchangeGroupOutcome.Confirmed
                ? "The exchange group confirmed on chain."
                : "The exchange group was accepted and has not confirmed yet.");
    }

    // ── The provider-bound, multi-signer shape ────────────────────────────────

    /// <summary>
    /// Rebinds the plan to a RESOLVED provider — the only thing that knows which chain and
    /// network this node would actually broadcast to — and marks every leg not sent from the fee
    /// payer as fee-pooled.
    /// </summary>
    private AZOAResult<MultiSignerAtomicGroupRequest> BuildRequest(ExchangeGroupPlan plan)
    {
        IBlockchainProvider provider;
        try
        {
            provider = _providers.GetProvider(plan.ChainType, plan.Network);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AZOAResult<MultiSignerAtomicGroupRequest>.Failure(
                $"No blockchain provider resolved for {plan.ChainType}/{plan.Network}: {exception.Message}");
        }

        var legs = plan.Legs
            .Select(leg => new MultiSignerAtomicGroupLeg(
                leg.AssetId,
                leg.FromAddress,
                leg.ToAddress,
                leg.Amount,
                leg.SigningContext,
                FeeIsPooled: !string.Equals(leg.FromAddress, plan.FeePayerAddress, StringComparison.Ordinal)))
            .ToArray();

        return MultiSignerAtomicGroupRequest.TryCreate(
            provider,
            plan.ChainType,
            plan.Network,
            plan.IdempotencyKey,
            legs,
            plan.FeePayerAddress,
            plan.FeePayerSigningContext);
    }

    // ── Assembly ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the transactions, binds them into one chain group and assigns the pooled fee.
    /// Nothing here touches a key, so an assembly failure has moved nothing.
    /// </summary>
    private AZOAResult<AssembledExchangeGroup> AssembleGroup(
        ExchangeGroupPlan plan,
        MultiSignerAtomicGroupRequest request,
        AlgodSuggestedParameters suggested)
    {
        var needsFeePayerLeg = request.NeedsSeparateFeePayerLeg;
        var total = request.Legs.Count + (needsFeePayerLeg ? 1 : 0);
        if (total > MultiSignerAtomicGroupRequest.MaxLegs)
        {
            return AZOAResult<AssembledExchangeGroup>.Failure(
                $"An exchange group of {total} transactions is larger than one atomic group may carry.");
        }

        if (!Address.IsValid(request.FeePayerAddress))
            return AZOAResult<AssembledExchangeGroup>.Failure("The exchange fee payer is not a valid address.");

        var transactions = new List<Transaction>(total);
        var signers = new List<ExchangeLegSigner>(total);
        var senders = new List<string>(total);

        for (var index = 0; index < request.Legs.Count; index++)
        {
            var leg = request.Legs[index];
            var declaredSigner = plan.Legs[index].Signer;

            // The declared signer and the account must agree. The reserve and a project treasury
            // are BOTH node-held platform contexts, so the address is the only thing that tells
            // their keys apart — and signing a treasury's money with the reserve's key would be a
            // transaction the treasury never made.
            var senderIsFeePayer = string.Equals(
                leg.FromAddress, request.FeePayerAddress, StringComparison.Ordinal);
            if (declaredSigner == ExchangeLegSigner.PlatformReserve && !senderIsFeePayer)
            {
                return AZOAResult<AssembledExchangeGroup>.Failure(
                    "An exchange leg declared as the reserve's is not sent from the reserve account.");
            }

            if (declaredSigner == ExchangeLegSigner.ProjectTreasury && senderIsFeePayer)
            {
                return AZOAResult<AssembledExchangeGroup>.Failure(
                    "An exchange leg declared as a project treasury's is sent from the reserve account.");
            }

            if (!ulong.TryParse(leg.AssetId, NumberStyles.None, CultureInfo.InvariantCulture, out var assetId)
                || assetId == 0)
            {
                return AZOAResult<AssembledExchangeGroup>.Failure(
                    "An Algorand exchange leg requires a numeric ASA asset id.");
            }

            if (!Address.IsValid(leg.FromAddress) || !Address.IsValid(leg.ToAddress))
            {
                return AZOAResult<AssembledExchangeGroup>.Failure(
                    "An Algorand exchange leg requires valid source and destination addresses.");
            }

            transactions.Add(new AssetTransferTransaction
            {
                Sender = new Address(leg.FromAddress),
                XferAsset = assetId,
                AssetReceiver = new Address(leg.ToAddress),
                AssetAmount = leg.AmountBaseUnits,
            });
            signers.Add(declaredSigner);
            senders.Add(leg.FromAddress);
        }

        if (needsFeePayerLeg)
        {
            // Carries nothing but the group's pooled fee. A zero-value self-payment moves no
            // value and changes no balance beyond the fee the reserve was always going to pay.
            var feePayer = new Address(request.FeePayerAddress);
            transactions.Add(new PaymentTransaction
            {
                Sender = feePayer,
                Receiver = feePayer,
                Amount = 0,
            });
            signers.Add(ExchangeLegSigner.PlatformReserve);
            senders.Add(request.FeePayerAddress);
        }

        var suggestedFee = (ulong)Math.Max(0L, Math.Max(suggested.MinFee, suggested.Fee));
        var perTransactionFee = Math.Max(suggestedFee, _options.EffectiveMinimumFeeMicroAlgos);
        var pooledFee = checked(perTransactionFee * (ulong)transactions.Count);
        var firstValid = (ulong)Math.Max(0L, suggested.LastRound);
        var lastValid = firstValid + _options.EffectiveValidityWindowRounds;

        var feeCarrier = senders.FindIndex(sender =>
            string.Equals(sender, request.FeePayerAddress, StringComparison.Ordinal));
        if (feeCarrier < 0)
        {
            return AZOAResult<AssembledExchangeGroup>.Failure(
                "The assembled exchange group has no transaction the fee payer signs, so its fee "
                + "cannot be pooled.");
        }

        for (var index = 0; index < transactions.Count; index++)
        {
            var transaction = transactions[index];
            transaction.FirstValid = firstValid;
            transaction.LastValid = lastValid;
            transaction.GenesisId = suggested.GenesisId;
            if (!string.IsNullOrWhiteSpace(suggested.GenesisHashB64))
                transaction.GenesisHash = new Digest(suggested.GenesisHashB64);

            // Pooling, written on the field the chain actually reads: every other transaction
            // pays nothing, and the fee payer's transaction pays for the whole group.
            transaction.Fee = index == feeCarrier ? pooledFee : 0UL;
        }

        var ordered = transactions.ToArray();
        TxGroup.AssignGroupID(ordered);
        var groupId = ordered[0].Group?.ToString();
        if (string.IsNullOrWhiteSpace(groupId)
            || ordered.Any(transaction =>
                transaction.Group is null || !transaction.Group.Equals(ordered[0].Group)))
        {
            return AZOAResult<AssembledExchangeGroup>.Failure(
                "Algorand group-id assignment did not bind every exchange leg to one group.");
        }

        var transactionIds = ordered.Select(transaction => transaction.TxID()).ToArray();
        if (transactionIds.Any(string.IsNullOrWhiteSpace)
            || transactionIds.Distinct(StringComparer.Ordinal).Count() != transactionIds.Length)
        {
            return AZOAResult<AssembledExchangeGroup>.Failure(
                "Algorand did not assign a distinct transaction id to every exchange leg.");
        }

        return AZOAResult<AssembledExchangeGroup>.Success(new AssembledExchangeGroup(
            ordered,
            signers,
            senders,
            transactionIds,
            transactionIds.Take(request.Legs.Count).ToArray(),
            groupId!,
            lastValid));
    }

    // ── Signing ───────────────────────────────────────────────────────────────

    /// <summary>Signs every leg through its own custody door, or refuses the whole group.</summary>
    private async Task<AZOAResult<byte[]>> SignGroupAsync(
        ExchangeGroupPlan plan, AssembledExchangeGroup group, CancellationToken ct)
    {
        if (!_signers.TryGetSigner(SupportedChainType, out var signer) || signer is null)
            return AZOAResult<byte[]>.Failure($"No transaction signer is registered for {SupportedChainType}.");

        var envelopes = new byte[group.Transactions.Length][];
        for (var index = 0; index < group.Transactions.Length; index++)
        {
            ct.ThrowIfCancellationRequested();

            var context = index < plan.Legs.Count
                ? plan.Legs[index].SigningContext
                : plan.FeePayerSigningContext;

            var signed = await SignOneAsync(
                plan, signer, group.Transactions[index], group.Senders[index], group.Signers[index], context);
            if (signed.IsError || signed.Result is null || signed.Result.Length == 0)
            {
                return AZOAResult<byte[]>.Failure(
                    string.IsNullOrWhiteSpace(signed.Message) ? "An exchange leg could not be signed." : signed.Message);
            }

            envelopes[index] = signed.Result;
        }

        var payload = new byte[envelopes.Sum(envelope => envelope.Length)];
        var offset = 0;
        foreach (var envelope in envelopes)
        {
            Buffer.BlockCopy(envelope, 0, payload, offset, envelope.Length);
            offset += envelope.Length;
        }

        return AZOAResult<byte[]>.Success(payload);
    }

    private async Task<AZOAResult<byte[]>> SignOneAsync(
        ExchangeGroupPlan plan,
        ITransactionSigner signer,
        Transaction transaction,
        string expectedSigningAddress,
        ExchangeLegSigner legSigner,
        SigningContext context)
    {
        var canonical = Encoder.EncodeToMsgPackOrdered(transaction);

        // Runs inside the custody seam's decrypt-sign-zero scope. The address check is what makes
        // the signer discriminator a control rather than a label: a key that does not hold the
        // account the leg sends from never signs, whatever the discriminator claimed.
        Task<AZOAResult<byte[]>> Sign(byte[] keyBytes)
        {
            using var material = new SigningKeyMaterial(keyBytes);
            var signingAddress = new AlgoAccount(keyBytes).Address.EncodeAsString();
            if (!string.Equals(signingAddress, expectedSigningAddress, StringComparison.Ordinal))
            {
                return Task.FromResult(AZOAResult<byte[]>.Failure(
                    "The key resolved for an exchange leg does not hold the account that leg sends "
                    + "from; refusing to sign it."));
            }

            return Task.FromResult(signer.Sign(canonical, material));
        }

        Func<byte[], Task<AZOAResult<byte[]>>> sign = Sign;

        switch (legSigner)
        {
            case ExchangeLegSigner.Recipient:
                return Flatten(await _custody.WithSigningKeyAsync(context, sign));

            case ExchangeLegSigner.PlatformReserve:
                return Flatten(await _reserve.SignAsReserveAsync(plan.ChainType, context, sign));

            case ExchangeLegSigner.ProjectTreasury:
                if (plan.TreasuryReference is null)
                {
                    return AZOAResult<byte[]>.Failure(
                        "This exchange pays out of a project treasury but does not name the project whose "
                        + "treasury key signs it.");
                }

                return Flatten(await _treasury.SignAsTreasuryAsync(plan.TreasuryReference, context, sign));

            default:
                return AZOAResult<byte[]>.Failure($"Unsupported exchange leg signer '{legSigner}'.");
        }
    }

    private static AZOAResult<byte[]> Flatten(AZOAResult<AZOAResult<byte[]>> outer) =>
        outer.IsError || outer.Result is null
            ? AZOAResult<byte[]>.Failure(
                string.IsNullOrWhiteSpace(outer.Message) ? "An exchange leg could not be signed." : outer.Message)
            : outer.Result;

    // ── Broadcast and observation ─────────────────────────────────────────────

    private static async Task<AZOAResult<bool>> BroadcastAsync(
        HttpClient algod, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
            using var response = await algod.PostAsync("/v2/transactions", content, ct);
            response.EnsureSuccessStatusCode();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return AZOAResult<bool>.Failure(
                "The exchange group was not accepted for broadcast; nothing was moved: " + exception.Message);
        }
    }

    /// <summary>
    /// Reads the accepted group back until it confirms or can no longer commit.
    /// <para>
    /// Only a COMPLETE observation may report a drop. If any read failed, the fate of a group the
    /// rail already holds is unknown, and calling it dropped would unwind an exchange that might
    /// settle a round later.
    /// </para>
    /// </summary>
    private async Task<ExchangeGroupOutcome> ObserveAsync(
        HttpClient algod, AssembledExchangeGroup group, CancellationToken ct)
    {
        var attempts = _options.EffectiveConfirmationPollAttempts;
        var delay = _options.EffectiveConfirmationPollDelayMilliseconds;
        var everyReadSucceeded = true;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var confirmedLegs = 0;
            foreach (var transactionId in group.TransactionIds)
            {
                var leg = await ReadLegAsync(algod, transactionId, ct);
                if (leg is null)
                {
                    everyReadSucceeded = false;
                    continue;
                }

                if (leg.Rejected)
                {
                    _logger?.LogWarning(
                        "Exchange group {GroupId} was rejected by the Algorand pool: {PoolError}",
                        group.ChainGroupId, leg.PoolError);
                    return ExchangeGroupOutcome.DroppedAfterAcceptance;
                }

                if (leg.Confirmed)
                    confirmedLegs++;
            }

            // The legs land together or not at all, so a partial reading is an artefact of
            // reading them one at a time; the loop keeps going until all of them agree.
            if (confirmedLegs == group.TransactionIds.Length)
                return ExchangeGroupOutcome.Confirmed;

            var round = await ReadCurrentRoundAsync(algod, ct);
            if (round is null)
            {
                everyReadSucceeded = false;
            }
            else if (round > group.LastValidRound)
            {
                // Past its validity window a transaction can never be committed. That, and only
                // that, together with a complete observation, is what justifies an unwind.
                return everyReadSucceeded
                    ? ExchangeGroupOutcome.DroppedAfterAcceptance
                    : ExchangeGroupOutcome.Accepted;
            }

            await Task.Delay(delay, ct);
        }

        return ExchangeGroupOutcome.Accepted;
    }

    private async Task<AlgodLegObservation?> ReadLegAsync(
        HttpClient algod, string transactionId, CancellationToken ct)
    {
        try
        {
            var pending = await algod.GetFromJsonAsync<AlgodPendingTransaction>(
                $"/v2/transactions/pending/{transactionId}", cancellationToken: ct);
            return new AlgodLegObservation(
                Confirmed: pending?.ConfirmedRound > 0,
                Rejected: !string.IsNullOrWhiteSpace(pending?.PoolError),
                PoolError: pending?.PoolError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Neither in the pool nor recently committed. On its own that is a COMPLETE negative
            // read; whether it is fatal depends on the round, which the caller checks.
            return new AlgodLegObservation(Confirmed: false, Rejected: false, PoolError: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            _logger?.LogWarning(
                exception, "Could not read Algorand transaction {TransactionId} back.", transactionId);
            return null;
        }
    }

    private async Task<ulong?> ReadCurrentRoundAsync(HttpClient algod, CancellationToken ct)
    {
        var suggested = await ReadSuggestedParametersAsync(algod, ct);
        return suggested.IsError || suggested.Result is null
            ? null
            : (ulong)Math.Max(0L, suggested.Result.LastRound);
    }

    private static async Task<AZOAResult<AlgodSuggestedParameters>> ReadSuggestedParametersAsync(
        HttpClient algod, CancellationToken ct)
    {
        try
        {
            var parameters = await algod.GetFromJsonAsync<AlgodSuggestedParameters>(
                "/v2/transactions/params", cancellationToken: ct);
            return parameters is null
                ? AZOAResult<AlgodSuggestedParameters>.Failure("Algod returned no suggested parameters.")
                : AZOAResult<AlgodSuggestedParameters>.Success(parameters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or System.Text.Json.JsonException
                                              or NotSupportedException)
        {
            return AZOAResult<AlgodSuggestedParameters>.Failure(
                "Algod suggested parameters could not be read: " + exception.Message);
        }
    }

    private HttpClient? CreateAlgodClient(ExchangeGroupPlan plan)
    {
        BlockchainNetworkConfig config;
        try
        {
            config = _chainConfiguration.GetNetworkConfig(plan.ChainType, plan.Network);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogError(
                exception, "No algod configuration for {Chain}/{Network}.", plan.ChainType, plan.Network);
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.NodeUrl))
            return null;

        var client = _httpClients.CreateClient(AlgodHttpClientName);
        client.BaseAddress = new Uri(config.NodeUrl);
        client.Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 30_000);
        if (!string.IsNullOrWhiteSpace(config.ApiToken))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Algo-API-Token", config.ApiToken);
        return client;
    }

    /// <summary>
    /// Every refusal a consumer could see. Rail text names accounts, assets and acceptance
    /// steps, so it is replaced wholesale rather than trimmed, and the detail is logged instead.
    /// </summary>
    private AZOAResult<ExchangeGroupSubmission> Refuse(string? railMessage)
    {
        _logger?.LogWarning("An exchange group was refused before settling: {Detail}", railMessage);
        return AZOAResult<ExchangeGroupSubmission>.FailureWithCode(
            UnavailableMessage, ConsumerSafeErrorCodes.RailUnavailable);
    }

    // ── Local shapes ──────────────────────────────────────────────────────────

    /// <param name="PlanLegTransactionIds">Ids for the PLAN's legs only, in the plan's own order.</param>
    private sealed record AssembledExchangeGroup(
        Transaction[] Transactions,
        IReadOnlyList<ExchangeLegSigner> Signers,
        IReadOnlyList<string> Senders,
        string[] TransactionIds,
        IReadOnlyList<string> PlanLegTransactionIds,
        string ChainGroupId,
        ulong LastValidRound);

    private sealed record AlgodLegObservation(bool Confirmed, bool Rejected, string? PoolError);

    private sealed class AlgodSuggestedParameters
    {
        [JsonPropertyName("fee")]
        public long Fee { get; set; }

        [JsonPropertyName("min-fee")]
        public long MinFee { get; set; }

        [JsonPropertyName("last-round")]
        public long LastRound { get; set; }

        [JsonPropertyName("genesis-id")]
        public string? GenesisId { get; set; }

        [JsonPropertyName("genesis-hash")]
        public string? GenesisHashB64 { get; set; }
    }

    private sealed class AlgodPendingTransaction
    {
        [JsonPropertyName("confirmed-round")]
        public long ConfirmedRound { get; set; }

        [JsonPropertyName("pool-error")]
        public string? PoolError { get; set; }
    }
}
