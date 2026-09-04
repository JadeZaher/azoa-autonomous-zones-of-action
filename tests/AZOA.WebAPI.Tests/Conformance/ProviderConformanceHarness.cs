using System.Net;
using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;
using AZOA.WebAPI.Services.Signing;
using AlgorandAccount = Algorand.Algod.Model.Account;
using SolanaBip39Mnemonic = Solnet.Wallet.Bip39.Mnemonic;
using SolanaBip39WordCount = Solnet.Wallet.Bip39.WordCount;
using SolanaBip39WordList = Solnet.Wallet.Bip39.WordList;

namespace AZOA.WebAPI.Tests.Conformance;

/// <summary>
/// Construction and observation machinery for the provider-boundary conformance suite.
/// Rationale, enrollment rules, and the exemption policy live in
/// <c>tests/AZOA.WebAPI.Tests/Conformance/AGENTS.md</c>.
/// </summary>
public static class ProviderConformanceCatalog
{
    /// <summary>The harness binds every provider to devnet; no live network is ever contacted.</summary>
    public const ChainNetwork Network = ChainNetwork.Devnet;

    private const string NodeUrl = "http://conformance.invalid.node/";
    private const string IndexerUrl = "http://conformance.invalid.indexer/";
    private const string WalletEncryptionKey = "provider-conformance-wallet-encryption-key-0123456789";

    /// <summary>Every concrete blockchain provider shipped by the application assembly.</summary>
    public static IReadOnlyList<Type> DiscoverProviderTypes() =>
        typeof(BaseBlockchainProvider).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(t => typeof(IBlockchainProvider).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every capability module interface declared by the application assembly.</summary>
    public static IReadOnlyList<Type> DiscoverCapabilityModuleInterfaces() =>
        typeof(IBlockchainProviderModule).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t != typeof(IBlockchainProviderModule))
            .Where(t => typeof(IBlockchainProviderModule).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The chain type a provider reports, obtained from a throwaway instance built on an
    /// empty configuration. Constructors allocate no network clients (see
    /// <c>Providers/Blockchain/AGENTS.md</c> §network-instance-isolation), so this is safe
    /// and is what lets the harness configure a provider it has never heard of.
    /// </summary>
    public static string ReadChainType(Type providerType)
    {
        var probeConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZOA:WalletEncryptionKey"] = WalletEncryptionKey,
                [ChainNetworkResolution.DefaultNetworkConfigPath] = "devnet",
            })
            .Build();

        return Construct(providerType, probeConfiguration, transport: null, signerFactory: null).ChainType;
    }

    /// <summary>Builds a bound provider wired to the harness transport and signer.</summary>
    public static ProviderUnderTest Bind(Type providerType)
    {
        var chainType = ReadChainType(providerType);
        var transport = new ConformanceTransport();
        var signer = new RecordingSigner(RealSignerFor(chainType));
        var signerFactory = new TransactionSignerFactory(new ITransactionSigner[] { signer });
        var settings = BuildSettings(chainType);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var provider = Construct(providerType, configuration, transport, signerFactory);
        provider.Initialize(
            new BlockchainNetworkConfig
            {
                NodeUrl = NodeUrl,
                IndexerUrl = IndexerUrl,
                TimeoutMs = 2000,
                IsEnabled = true,
            },
            Network);

        return new ProviderUnderTest(
            providerType,
            chainType,
            provider,
            transport,
            signer,
            signerFactory,
            PlatformMaterialFor(chainType, settings));
    }

    // ─── Construction ───

    /// <summary>
    /// Builds a provider through the widest public constructor whose every parameter the
    /// harness can satisfy. A provider that cannot be built this way is NOT enrolled, which
    /// <see cref="ProviderCertificationReportTests"/> turns into a hard failure — a new chain
    /// cannot quietly opt out of the battery.
    /// </summary>
    private static IBlockchainProvider Construct(
        Type providerType,
        IConfiguration configuration,
        HttpMessageHandler? transport,
        ITransactionSignerFactory? signerFactory)
    {
        var failures = new List<string>();

        foreach (var ctor in providerType.GetConstructors()
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var arguments = new object?[parameters.Length];
            var satisfied = true;

            for (int i = 0; i < parameters.Length && satisfied; i++)
            {
                if (TryResolveArgument(parameters[i], providerType, configuration, transport, signerFactory, out var value))
                    arguments[i] = value;
                else
                    satisfied = false;
            }

            if (!satisfied)
            {
                failures.Add($"{parameters.Length}-arg ctor: unsatisfiable parameter");
                continue;
            }

            try
            {
                return (IBlockchainProvider)ctor.Invoke(arguments)!;
            }
            catch (TargetInvocationException ex)
            {
                failures.Add($"{parameters.Length}-arg ctor threw: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        throw new ProviderNotEnrollableException(providerType, failures);
    }

    private static bool TryResolveArgument(
        ParameterInfo parameter,
        Type providerType,
        IConfiguration configuration,
        HttpMessageHandler? transport,
        ITransactionSignerFactory? signerFactory,
        out object? value)
    {
        var type = parameter.ParameterType;
        value = null;

        if (type == typeof(IConfiguration))
        {
            value = configuration;
            return true;
        }

        if (type == typeof(ILogger) ||
            (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>)))
        {
            value = NullLoggerFor(type, providerType);
            return true;
        }

        if (typeof(HttpMessageHandler).IsAssignableFrom(type))
        {
            value = transport;
            return true;
        }

        if (type == typeof(ITransactionSignerFactory))
        {
            value = signerFactory;
            return true;
        }

        // A concrete collaborator the harness can build from configuration alone
        // (WalletKeyService is the shipped example).
        if (type is { IsClass: true, IsAbstract: false } &&
            type.GetConstructor(new[] { typeof(IConfiguration) }) is { } configCtor)
        {
            try
            {
                value = configCtor.Invoke(new object?[] { configuration });
                return true;
            }
            catch (TargetInvocationException)
            {
                value = null;
                // Fall through: the collaborator is optional on every shipped provider, so an
                // unbuildable one becomes absence rather than a harness failure.
            }
        }

        // Everything else is deliberately absent: no custody service, no scope factory,
        // no faucet. Absence is what several invariants are certified against.
        if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null)
            return true;

        if (parameter.HasDefaultValue)
        {
            value = parameter.DefaultValue;
            return true;
        }

        return false;
    }

    private static object NullLoggerFor(Type loggerParameterType, Type providerType)
    {
        if (loggerParameterType == typeof(ILogger))
            return NullLogger.Instance;

        var categoryType = loggerParameterType.GetGenericArguments()[0];
        var closed = typeof(NullLogger<>).MakeGenericType(categoryType.IsAssignableFrom(providerType) ? categoryType : providerType);
        return Activator.CreateInstance(closed)!;
    }

    // ─── Configuration ───

    /// <summary>
    /// Harness configuration for one chain. The retry/poll knobs and platform-key paths are
    /// discovered from the assembly's own <c>*ConfigPath</c> constants, so a new provider that
    /// follows the same naming convention is configured without editing this method.
    /// </summary>
    private static Dictionary<string, string?> BuildSettings(string chainType)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZOA:WalletEncryptionKey"] = WalletEncryptionKey,
            [ChainNetworkResolution.DefaultNetworkConfigPath] = "devnet",
            ["Blockchain:Chains:0:ChainType"] = chainType,
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true",
            ["Blockchain:Chains:0:Devnet:NodeUrl"] = NodeUrl,
            ["Blockchain:Chains:0:Devnet:IndexerUrl"] = IndexerUrl,
            ["Blockchain:Chains:0:Devnet:TimeoutMs"] = "2000",
        };

        foreach (var path in DiscoverConfigPaths())
        {
            var value = ConventionalValueFor(path, chainType);
            if (value is not null)
                settings[path] = value;
        }

        return settings;
    }

    /// <summary>
    /// Every <c>AZOA:</c>-rooted configuration path the application declares as a constant.
    /// Reading the constants rather than a hand-kept list is what keeps the harness honest
    /// when a provider adds a knob.
    /// </summary>
    public static IReadOnlyList<string> DiscoverConfigPaths()
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in typeof(BaseBlockchainProvider).Assembly.GetTypes())
        {
            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (TypeLoadException)
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (!field.IsLiteral || field.FieldType != typeof(string))
                    continue;
                if (field.GetRawConstantValue() is not string literal)
                    continue;
                if (literal.StartsWith("AZOA:", StringComparison.Ordinal))
                    paths.Add(literal);
            }
        }

        return paths.ToList();
    }

    /// <summary>
    /// Harness value for a discovered configuration path, chosen from the path's own last
    /// segment. Returning null leaves the key unset, which the certification report shows as
    /// "no platform material" rather than hiding it.
    /// </summary>
    private static string? ConventionalValueFor(string path, string chainType)
    {
        var leaf = path.Split(':').Last();

        if (leaf.Contains("Retry", StringComparison.Ordinal)) return "0";
        if (leaf.Contains("Attempts", StringComparison.Ordinal)) return "1";
        if (leaf.EndsWith("DelayMs", StringComparison.Ordinal)) return "1";
        if (leaf.EndsWith("PrivateKey", StringComparison.Ordinal)) return HarnessPlatformPrivateKey;
        if (leaf.EndsWith("Mnemonic", StringComparison.Ordinal)) return MnemonicFor(path);

        return null;
    }

    /// <summary>
    /// A deterministic, throwaway secp256k1 key. It authorises nothing: the harness transport
    /// is in-process and refuses every request.
    /// </summary>
    private const string HarnessPlatformPrivateKey =
        "0x4c0883a69102937d6231471b5dbb6204fe512961708279e2b1f3b7c1f4a3e5d9";

    private static readonly Lazy<string> AlgorandMnemonic =
        new(() => new AlgorandAccount().ToMnemonic());

    private static readonly Lazy<string> Bip39Mnemonic =
        new(() => new SolanaBip39Mnemonic(SolanaBip39WordList.English, SolanaBip39WordCount.Twelve).ToString());

    /// <summary>
    /// Platform mnemonics are not one format: Algorand's is a 25-word Algorand mnemonic,
    /// the BIP39 chains take twelve words. A path whose chain segment is unrecognised gets
    /// the BIP39 shape, and any resulting fail-closed refusal is reported truthfully.
    /// </summary>
    private static string MnemonicFor(string path) =>
        path.Contains(":Algorand:", StringComparison.OrdinalIgnoreCase)
            ? AlgorandMnemonic.Value
            : Bip39Mnemonic.Value;

    private static string? PlatformMaterialFor(string chainType, IReadOnlyDictionary<string, string?> settings)
    {
        var owned = settings.Keys
            .Where(k => k.Contains($":{chainType}:", StringComparison.OrdinalIgnoreCase))
            .Where(k => k.EndsWith("Mnemonic", StringComparison.Ordinal) || k.EndsWith("PrivateKey", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        return owned.Count == 0 ? null : string.Join(", ", owned);
    }

    // ─── Signing ───

    /// <summary>
    /// The real shipped signer for a chain, discovered from the assembly. A chain with no
    /// signer gets a refusing stand-in so "no signer is registered" stays a truthful message
    /// rather than a harness artefact.
    /// </summary>
    private static ITransactionSigner RealSignerFor(string chainType)
    {
        foreach (var type in typeof(BaseBlockchainProvider).Assembly.GetTypes())
        {
            if (type is not { IsClass: true, IsAbstract: false }) continue;
            if (!typeof(ITransactionSigner).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is not { } ctor) continue;

            var candidate = (ITransactionSigner)ctor.Invoke(null);
            if (string.Equals(candidate.ChainType, chainType, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return new UnregisteredChainSigner(chainType);
    }

    private sealed class UnregisteredChainSigner : ITransactionSigner
    {
        public UnregisteredChainSigner(string chainType) => ChainType = chainType;

        public string ChainType { get; }

        public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key) =>
            new() { IsError = true, Message = $"No transaction signer ships for chain type {ChainType}." };
    }
}

/// <summary>Thrown when a discovered provider cannot be built by the generic recipe.</summary>
public sealed class ProviderNotEnrollableException : Exception
{
    public ProviderNotEnrollableException(Type providerType, IEnumerable<string> attempts)
        : base($"{providerType.FullName} could not be constructed by the conformance harness. "
               + "Every shipped provider must be buildable from configuration, a logger, an optional "
               + "HttpMessageHandler and an optional ITransactionSignerFactory so it is enrolled "
               + "automatically. Attempts: " + string.Join(" | ", attempts))
    {
        ProviderType = providerType;
    }

    public Type ProviderType { get; }
}

/// <summary>One enrolled provider plus everything the battery observes about it.</summary>
public sealed class ProviderUnderTest
{
    public ProviderUnderTest(
        Type providerType,
        string chainType,
        IBlockchainProvider provider,
        ConformanceTransport transport,
        RecordingSigner signer,
        ITransactionSignerFactory signerFactory,
        string? platformMaterialPaths)
    {
        ProviderType = providerType;
        ChainType = chainType;
        Provider = provider;
        Transport = transport;
        Signer = signer;
        SignerFactory = signerFactory;
        PlatformMaterialPaths = platformMaterialPaths;
    }

    public Type ProviderType { get; }
    public string ChainType { get; }
    public IBlockchainProvider Provider { get; }
    public ConformanceTransport Transport { get; }
    public RecordingSigner Signer { get; }
    public ITransactionSignerFactory SignerFactory { get; }

    /// <summary>Which platform-key configuration paths the harness populated, if any.</summary>
    public string? PlatformMaterialPaths { get; }

    /// <summary>True when this provider never reaches a network at all (it has no transport).</summary>
    public bool HasTransport => Transport.SawAnyRequest;

    public bool Implements<TModule>() where TModule : class, IBlockchainProviderModule =>
        Provider.TryGetModule<TModule>(out var module) && module is not null;

    public override string ToString() => ChainType;
}

/// <summary>
/// The provider-agnostic transport: every request is recorded, and the answer is chosen by
/// <see cref="Mode"/>. No shape here is chain-specific, which is the point — a per-chain stub
/// belongs in that chain's own suite, not in the shared battery.
/// </summary>
public sealed class ConformanceTransport : HttpMessageHandler
{
    public enum ResponseMode
    {
        /// <summary>Every call fails with an ambiguous server error. Nothing can be confirmed.</summary>
        RefuseEverything,

        /// <summary>Every lookup is a clean miss. The honest "this chain has never seen it" answer.</summary>
        NotFound,

        /// <summary>
        /// HTTP 200 carrying a null result — the vacuous-null trap. A reader that tests
        /// <c>receipt?.Err == null</c> reads this as success even though there is no receipt.
        /// </summary>
        NullPayload,
    }

    private readonly List<RecordedRequest> _requests = new();
    private readonly object _gate = new();

    public ResponseMode Mode { get; set; } = ResponseMode.RefuseEverything;

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public int RequestCount
    {
        get { lock (_gate) return _requests.Count; }
    }

    public bool SawAnyRequest => RequestCount > 0;

    public void Reset()
    {
        lock (_gate) _requests.Clear();
    }

    /// <summary>How many recorded request bodies carry <paramref name="payload"/> verbatim.</summary>
    public int RequestsCarrying(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return 0;
        lock (_gate)
            return _requests.Count(r => r.Body.Contains(payload, StringComparison.Ordinal));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (_gate)
            _requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri?.ToString() ?? "", body));

        return Mode switch
        {
            ResponseMode.RefuseEverything => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"conformance transport refuses every request\"}}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            },
            ResponseMode.NotFound => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"not found\"}", System.Text.Encoding.UTF8, "application/json"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}", System.Text.Encoding.UTF8, "application/json"),
            },
        };
    }

    public sealed record RecordedRequest(string Method, string Uri, string Body);
}

/// <summary>
/// Wraps the chain's real signer so the battery can prove two things it cannot see otherwise:
/// whether a value path ever reached signing at all, and whether the signed bytes went onto
/// the wire more than once.
/// </summary>
public sealed class RecordingSigner : ITransactionSigner
{
    private readonly ITransactionSigner _inner;
    private readonly List<byte[]> _signedOutputs = new();
    private readonly object _gate = new();

    public RecordingSigner(ITransactionSigner inner) => _inner = inner;

    public string ChainType => _inner.ChainType;

    public int SignCount { get; private set; }

    public IReadOnlyList<byte[]> SignedOutputs
    {
        get { lock (_gate) return _signedOutputs.ToList(); }
    }

    public void Reset()
    {
        lock (_gate)
        {
            SignCount = 0;
            _signedOutputs.Clear();
        }
    }

    public AZOAResult<byte[]> Sign(byte[] canonicalTxn, SigningKeyMaterial key)
    {
        lock (_gate) SignCount++;

        var result = _inner.Sign(canonicalTxn, key);
        if (!result.IsError && result.Result is { Length: > 0 })
            lock (_gate) _signedOutputs.Add((byte[])result.Result.Clone());

        return result;
    }

    /// <summary>
    /// Every wire encoding a provider might submit signed bytes under. A re-broadcast is the
    /// same bytes a second time, whichever encoding the chain's RPC takes.
    /// </summary>
    public IEnumerable<string> WireEncodings()
    {
        foreach (var bytes in SignedOutputs)
        {
            yield return Convert.ToBase64String(bytes);
            yield return Convert.ToHexString(bytes).ToLowerInvariant();
            yield return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}

/// <summary>Amounts the battery pushes through every provider's value paths.</summary>
public static class ConformanceAmounts
{
    /// <summary>Above the 2^53 double ceiling — the first value a JSON number would corrupt.</summary>
    public static readonly BigInteger AboveDoublePrecision = BigInteger.Pow(2, 53) + 1;

    /// <summary>The old <c>ulong</c> interface ceiling that capped the whole surface at ~18.44 ETH.</summary>
    public static readonly BigInteger UlongCeiling = ulong.MaxValue;

    /// <summary>18.5 ETH in wei — just past the old ceiling, the value that used to be unrepresentable.</summary>
    public static readonly BigInteger AboveUlongCeiling = BigInteger.Parse("18500000000000000000");

    /// <summary>A full uint256-shaped balance, far beyond any 64-bit surface.</summary>
    public static readonly BigInteger Uint256Shaped = BigInteger.Pow(2, 200);
}
