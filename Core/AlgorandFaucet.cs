using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Providers.Blockchain.Base;

namespace AZOA.WebAPI.Core;

/// <summary>
/// Dispenses test ALGO via the Algorand2 SDK (build → sign → submit).
/// The faucet account is loaded from a 25-word mnemonic in configuration
/// (Blockchain:Faucet:Algorand:Mnemonic). Used for dev / test networks only.
///
/// This is the one real server-side broadcaster in the API, so the on-chain
/// submit is guarded by the idempotency ledger: a retried / concurrent
/// <c>POST /topup</c> for the same logical dispense submits the payment
/// EXACTLY ONCE and replays the original txid on every duplicate.
/// </summary>
public class AlgorandFaucet : IAlgorandFaucet
{
    private readonly IConfiguration _config;
    private readonly ILogger<AlgorandFaucet> _logger;
    private readonly BlockchainConfigurationManager _configManager;
    // AlgorandFaucet is a singleton; IIdempotencyStore is scoped. Resolve it
    // per-call from a fresh scope (same pattern as ApiKeyAuthenticationHandler)
    // so we never capture a scoped dependency in a singleton.
    private readonly IServiceScopeFactory _scopeFactory;

    private const string FaucetChain = "Algorand";
    private const string FaucetOperationType = "faucet-dispense";

    public AlgorandFaucet(
        IConfiguration config,
        ILogger<AlgorandFaucet> logger,
        IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _configManager = new BlockchainConfigurationManager(config);
    }

    private string? Mnemonic => _config.GetValue<string>("Blockchain:Faucet:Algorand:Mnemonic");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Mnemonic);

    /// <summary>
    /// Back-compat overload. Derives a DETERMINISTIC idempotency key from the
    /// logical dispense inputs (chain + recipient + amount) — NOT a GUID or
    /// timestamp — so even old callers that cannot supply a key are deduped:
    /// a retried dispense of the same amount to the same address submits once.
    /// </summary>
    public Task<string> DispenseAsync(string toAddress, decimal amountAlgo, CancellationToken ct = default)
    {
        // Content-addressed key: same (chain, recipient, amount) ⇒ same key,
        // forever. OperationIdGenerator is fully deterministic (Wave 1).
        // Format the amount with InvariantCulture so the key is locale-stable
        // (decimal.ToString() is culture-sensitive — never feed it raw).
        var amountToken = amountAlgo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var idempotencyKey = OperationIdGenerator.Generate(
            FaucetChain, FaucetOperationType, toAddress, amountToken);
        return DispenseAsync(toAddress, amountAlgo, idempotencyKey, ct);
    }

    public async Task<string> DispenseAsync(
        string toAddress, decimal amountAlgo, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
            throw new ArgumentException("Recipient address is required.", nameof(toAddress));
        if (amountAlgo <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(amountAlgo));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        var mnemonic = Mnemonic;
        if (string.IsNullOrWhiteSpace(mnemonic))
            throw new InvalidOperationException(
                "Algorand faucet is not configured (set Blockchain:Faucet:Algorand:Mnemonic).");

        using var scope = _scopeFactory.CreateScope();
        var idempotencyStore = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();

        // Claim BEFORE building/signing/submitting. The UNIQUE-key insert is
        // the serialization point: under concurrent identical requests exactly
        // one caller gets Won=true, so exactly one on-chain submit happens.
        var claim = await idempotencyStore.TryClaimAsync(idempotencyKey, FaucetOperationType, ct);
        if (!claim.Won)
        {
            // Duplicate / concurrent dispense. MUST NOT submit again.
            var record = claim.Record;
            switch (record.State)
            {
                case IdempotencyState.Completed when !string.IsNullOrEmpty(record.ResultPayload):
                    _logger.LogInformation(
                        "Algorand faucet dispense deduplicated (key {Key}); replaying prior tx {TxId}",
                        idempotencyKey, record.ResultPayload);
                    return record.ResultPayload!;

                case IdempotencyState.Failed:
                    throw new InvalidOperationException(
                        $"A prior faucet dispense for this request failed and was not retried: {record.Error}");

                default:
                    // InProgress (or Completed with no cached txid — should not
                    // happen): the original submit may be in flight. Do NOT
                    // submit again; force the caller to poll/reconcile.
                    throw new InvalidOperationException(
                        "A faucet dispense for this request is already in progress; not re-submitting.");
            }
        }

        try
        {
            var network = _configManager.GetDefaultNetwork(FaucetChain);
            var networkConfig = _configManager.GetNetworkConfig(FaucetChain, network);

            var faucetAccount = new Account(mnemonic.Trim());

            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(networkConfig.NodeUrl),
                Timeout = TimeSpan.FromMilliseconds(networkConfig.TimeoutMs ?? 30000)
            };
            if (!string.IsNullOrWhiteSpace(networkConfig.ApiToken))
                httpClient.DefaultRequestHeaders.Add("X-Algo-API-Token", networkConfig.ApiToken);

            var algod = new DefaultApi(httpClient);

            var txParams = await algod.TransactionParamsAsync(ct);

            var microAlgos = Algorand.Utils.Utils.AlgosToMicroalgos((double)amountAlgo);

            var payment = PaymentTransaction.GetPaymentTransactionFromNetworkTransactionParameters(
                faucetAccount.Address,
                new Address(toAddress),
                microAlgos,
                "AZOA faucet top-up",
                txParams);

            var signedTx = payment.Sign(faucetAccount);

            // The single, irreversible on-chain effect. Reached only by the
            // claim winner.
            var response = await Algorand.Utils.Utils.SubmitTransaction(algod, signedTx);

            _logger.LogInformation(
                "Algorand faucet dispensed {Amount} ALGO to {To} on {Network} (tx {TxId})",
                amountAlgo, toAddress, network, response.Txid);

            await idempotencyStore.CompleteAsync(idempotencyKey, response.Txid, ct);
            return response.Txid;
        }
        catch (Exception ex)
        {
            // Record terminal failure so a same-key retry replays the failure
            // rather than blindly re-submitting. (Whether the submit actually
            // landed on-chain on an ambiguous failure is a reconciliation
            // concern — auto-retry is intentionally NOT done here.)
            await idempotencyStore.FailAsync(idempotencyKey, ex.Message, CancellationToken.None);
            _logger.LogError(ex,
                "Algorand faucet dispense failed for {To} (key {Key})", toAddress, idempotencyKey);
            throw;
        }
    }
}
