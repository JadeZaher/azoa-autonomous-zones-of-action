using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Models.Dex;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    private const string DexReceiptFormat = "ethereum-dex-receipt/1";

    private Task<SwapStageObservation> DexEffectObservationAsync(SwapIntent intent, SwapStagePlan plan,
        DexEvmFinalized finalized, CancellationToken ct)
        => DexReceiptObservationAsync(intent, plan, finalized.Candidate.TransactionIds, finalized.Receipt, null, ct);

    /// <summary>Classifies one finalized receipt for this plan; annotations (operator audit, reviewed evidence) join the evidence.</summary>
    private async Task<SwapStageObservation> DexReceiptObservationAsync(SwapIntent intent, SwapStagePlan plan, IReadOnlyList<string> ids,
        EthereumFinalizedReceipt receipt, IReadOnlyDictionary<string, object>? annotations, CancellationToken ct)
    {
        var quote = ReadDexAcceptedIntent(intent);
        var id = ids[0];
        var protocol = intent.Terms.Network.ProtocolIdentity;
        var evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Receipt"] = new
            {
                Format = DexReceiptFormat, plan.Stage, plan.Generation, PlanHash = CanonicalJson.Hash(plan), TransactionId = id,
                receipt.BlockHash, BlockNumber = Text(receipt.BlockNumber), receipt.NetworkFee, receipt.Succeeded,
            },
        };
        foreach (var (name, value) in annotations ?? new Dictionary<string, object>()) evidence[name] = value;
        if (!receipt.Succeeded)
            return new("FailedOnChain", ids, protocol, "0", "0", "0", receipt.NetworkFee, "0", Evidence(evidence),
                "The finalized transaction reverted; consumed network fees are retained and no swap effect occurred.");
        var logs = EthereumDexReceipt.Logs(receipt.Receipt, id, receipt.BlockHash);
        if (logs is null) return DexNeedsOperator(intent, ids, "Finalized receipt logs are malformed.");
        var owner = intent.Terms.Owner.Address.ToLowerInvariant();
        var at = new { blockHash = receipt.BlockHash, requireCanonical = true };
        if (plan.Stage == "Swap")
        {
            var terms = intent.Terms;
            var expectation = DexSwapExpectation(intent, quote);
            var gross = expectation.Gross; var fee = expectation.Fee; var treasury = expectation.Treasury;
            var output = EthereumDexReceipt.SwapOutput(logs, expectation);
            if (output is not { } actual || actual < EthereumDexExecutionCodec.Amount(terms.MinimumOutput))
                return DexNeedsOperator(intent, ids, "Successful receipt does not prove the exact executor, pool and token settlement grammar.");
            evidence["Settlement"] = new { GrossInput = Text(gross), PlatformFee = Text(fee), Output = Text(actual), quote.Pool,
                Executor = quote.Deployment.Executor.ToLowerInvariant(), Treasury = treasury.ToLowerInvariant() };
            if (!terms.InputAsset.Native) await AddAllowanceEvidenceAsync(evidence, intent, quote, plan.Stage, id, receipt, at, ct);
            return new("Confirmed", ids, protocol, Text(gross), Text(actual), Text(fee), receipt.NetworkFee, "0", Evidence(evidence));
        }
        var expected = plan.Stage == "ApproveExact" ? EthereumDexExecutionCodec.Amount(intent.Terms.GrossInput) : BigInteger.Zero;
        if (!EthereumDexReceipt.ExactApproval(logs, quote.Input.Address, owner, quote.Deployment.Executor, expected))
            return DexNeedsOperator(intent, ids, "Successful receipt does not prove the exact owner allowance change.");
        await AddAllowanceEvidenceAsync(evidence, intent, quote, plan.Stage, id, receipt, at, ct);
        return new("Confirmed", ids, protocol, "0", "0", "0", receipt.NetworkFee, "0", Evidence(evidence));
    }

    private async Task AddAllowanceEvidenceAsync(Dictionary<string, object> evidence, SwapIntent intent, DexEvmQuoteEvidence quote,
        string stage, string transactionId, EthereumFinalizedReceipt receipt, object at, CancellationToken ct)
    {
        var owner = intent.Terms.Owner.Address.ToLowerInvariant();
        var after = await ReadUInt256AtBlockAsync(quote.Input.Address, "allowance(address,address)", [owner, quote.Deployment.Executor], at, ct);
        evidence["AllowanceAfter"] = Text(after);
        if (after != 0 || stage is not ("Swap" or "RevokeAllowance")) return;
        var resource = DexAllowance(intent, quote);
        evidence["AllowanceRelease"] = new EthereumDexAllowanceReleaseEvidence(1, resource.ProtocolIdentity, resource.OwnerAddress,
            resource.TokenAddress, resource.ExecutorAddress, "0", transactionId, receipt.BlockHash, Text(receipt.BlockNumber), stage);
    }

    private static EthereumDexSwapExpectation DexSwapExpectation(SwapIntent intent, DexEvmQuoteEvidence quote)
    {
        var terms = intent.Terms;
        var treasury = string.IsNullOrEmpty(terms.PlatformFee.TreasuryAddress) ? "0x" + new string('0', 40) : terms.PlatformFee.TreasuryAddress;
        return new(quote.Deployment.Executor, quote.Deployment.Router, quote.Pool, terms.Owner.Address.ToLowerInvariant(), terms.RecipientAddress,
            quote.Input.Address, quote.Output.Address, treasury, EthereumDexExecutionCodec.Amount(terms.GrossInput),
            EthereumDexExecutionCodec.Amount(terms.PlatformFee.Amount), EthereumDexExecutionCodec.OperationIdentity(intent.OperationId),
            intent.QuoteHash, terms.PlatformFee.ScheduleVersion, terms.InputAsset.Native, terms.OutputAsset.Native);
    }

    private static string Evidence(Dictionary<string, object> evidence)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence));
}
