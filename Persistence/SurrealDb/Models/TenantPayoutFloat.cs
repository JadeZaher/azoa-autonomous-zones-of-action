using System.Security.Cryptography;
using SurrealForge.Client;
using SurrealForge.Client.Schema;

namespace AZOA.WebAPI.Persistence.SurrealDb.Models;

/// <summary>D-8: one tenant's payout float in one currency and payment environment.</summary>
[SurrealTable("tenant_payout_float", Aggregate = "TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)",
    Guardrail = "G6 SCHEMAFULL; one row per (tenant, currency, environment); counters move only under a version CAS together with the entry that justifies them")]
[SurrealNote("available = credited - finalized - reserved. credited is the sum of AVAILABLE tenant_payout_credit rows net of their reversals; reserved and finalized are the sums over tenant_payout_debit rows in those states net of transfer reversals. See Services/Fiat/OffRamp/AGENTS.md §tenant-funded.")]
[Slice("identity")]
[Index("tenant_payout_float_scope", Fields = new[] { "tenant_id", "currency_code", "environment" }, Unique = true)]
public sealed class TenantPayoutFloat : ISurrealRecord
{
    public const string SchemaNameConst = "tenant_payout_float";
    public string SchemaName => SchemaNameConst;

    [Id] public string Id { get; set; } = string.Empty;
    [References(typeof(User))] public string TenantId { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string CurrencyCode { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string Environment { get; set; } = string.Empty;
    public long CreditedMinorUnits { get; set; }
    public long ReservedMinorUnits { get; set; }
    public long FinalizedMinorUnits { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Money a new payout may still reserve; negative is a deficit.</summary>
    public long Available() => CreditedMinorUnits - FinalizedMinorUnits - ReservedMinorUnits;

    /// <summary>Deterministic record id: hex SHA-256 of tenant|currency|environment.</summary>
    public static string RecordIdFor(Guid tenantId, string currencyCode, string environment)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"tenant-payout-float|{tenantId:N}|{currencyCode.Trim().ToUpperInvariant()}|{environment.Trim()}"))).ToLowerInvariant();
}

/// <summary>D-8: one settled tenant top-up, credited to the float only once matured.</summary>
[SurrealTable("tenant_payout_credit", Aggregate = "TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)",
    Guardrail = "G6 SCHEMAFULL; record id derives from the checkout intent, so one credit per top-up")]
[SurrealNote("net_minor_units is Stripe's balance_transaction.net (the tenant bears processing fees). reversed_minor_units is the authoritative refunded + dispute-withdrawn total re-read from Stripe (dispute fees included). A credit counts toward the float only in state Available: credited contribution = net - reversed, which may be negative. available_at absent means the rail declared no reversal window, and such a credit never matures.")]
[Slice("identity")]
[Index("tenant_payout_credit_intent", Fields = new[] { "intent_id" }, Unique = true)]
[Index("tenant_payout_credit_float", Fields = new[] { "float_id", "state" })]
[Index("tenant_payout_credit_payment", Fields = new[] { "payment_intent_id" })]
public sealed class TenantPayoutCredit : ISurrealRecord
{
    public const string SchemaNameConst = "tenant_payout_credit";
    public string SchemaName => SchemaNameConst;

    [Id] public string Id { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string IntentId { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string FloatId { get; set; } = string.Empty;
    [References(typeof(User))] public string TenantId { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string CurrencyCode { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string Environment { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string PaymentIntentId { get; set; } = string.Empty;
    public string? ChargeId { get; set; }
    public long GrossMinorUnits { get; set; }
    public long NetMinorUnits { get; set; }
    public long ReversedMinorUnits { get; set; }
    [Inside("Pending", "Available")] public string State { get; set; } = "Pending";
    public DateTimeOffset? AvailableAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>What this credit adds to <c>credited</c> once Available.</summary>
    public long Contribution() => NetMinorUnits - ReversedMinorUnits;

    /// <summary>Deterministic record id: hex SHA-256 of the checkout intent.</summary>
    public static string RecordIdFor(string intentId)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"tenant-payout-credit|{intentId.Trim().ToLowerInvariant()}"))).ToLowerInvariant();
}

/// <summary>D-8: the one debit a tenant-funded cash-out holds against its float.</summary>
[SurrealTable("tenant_payout_debit", Aggregate = "TenantPayoutFunding (Services/Fiat/OffRamp/TenantPayoutFunding.cs)",
    Guardrail = "G6 SCHEMAFULL; record id = payout id and payout_id is UNIQUE, so one debit per cash-out")]
[SurrealNote("state: Reserved (coverage taken, money may or may not have left), Finalized (the bank payout is paid), Released (no transfer ever existed). reversed_minor_units = max(stored, Stripe amount_reversed) in Reserved and Finalized, so a transfer reversal or a failed-bank restitution gives the tenant back exactly what Stripe returned, once.")]
[Slice("identity")]
[Index("tenant_payout_debit_payout", Fields = new[] { "payout_id" }, Unique = true)]
[Index("tenant_payout_debit_float", Fields = new[] { "float_id", "state" })]
[Index("tenant_payout_debit_state", Fields = new[] { "state", "created_at" })]
public sealed class TenantPayoutDebit : ISurrealRecord
{
    public const string SchemaNameConst = "tenant_payout_debit";
    public string SchemaName => SchemaNameConst;

    [Id] public string Id { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string PayoutId { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string FloatId { get; set; } = string.Empty;
    [References(typeof(User))] public string TenantId { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string CurrencyCode { get; set; } = string.Empty;
    [Required(NotEmpty = true)] public string Environment { get; set; } = string.Empty;
    public long AmountMinorUnits { get; set; }
    public long ReversedMinorUnits { get; set; }
    [Inside("Reserved", "Finalized", "Released")] public string State { get; set; } = "Reserved";
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>What this debit holds against the float in its current state.</summary>
    public long Outstanding() => State == "Released" ? 0 : AmountMinorUnits - ReversedMinorUnits;
}
