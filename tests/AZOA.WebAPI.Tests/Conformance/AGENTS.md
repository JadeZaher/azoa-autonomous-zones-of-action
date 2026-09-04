---
type: Agent Notes
title: Provider boundary conformance harness
description: Why the shared conformance battery exists, how a provider is enrolled, and what an exemption costs.
---

# tests/AZOA.WebAPI.Tests/Conformance — agent notes

FR-6 of `conductor/tracks/provider-boundary-conformance_20260727/spec.md` requires a route to
pass conformance before it can be enabled. This directory is that gate.

## §what-this-is-not

It is not a second copy of the per-chain pipeline suites. `Signing/AlgorandProviderTransactTests.cs`,
`Signing/SolanaProviderSplPipelineTests.cs`, `Signing/EthereumProviderPipelineTests.cs` and
`Signing/BridgePrimitivesTests.cs` drive real build→sign→submit→confirm pipelines against
chain-specific stubs, and they stay where they are. This battery makes the invariants those
suites discovered **uniform and mandatory**: a chain added tomorrow inherits them without anyone
remembering to write the tests again.

## §enrollment — no opt-in, no opt-out

`ProviderConformanceCatalog.DiscoverProviderTypes()` reflects over the application assembly for
every concrete `IBlockchainProvider`. `Bind` then constructs each one through the widest public
constructor whose parameters the harness can satisfy — configuration, a logger, the shared
`HttpMessageHandler`, an `ITransactionSignerFactory` built from the real shipped signers, and any
collaborator constructible from `IConfiguration` alone. Everything else is deliberately left
absent, because several invariants are certified precisely against that absence (no custody
service ⇒ a per-user context has nowhere to fall back to).

A provider the harness cannot build fails `Every_shipped_provider_is_enrolled_automatically` by
name. That is the whole enrollment mechanism: adding a provider class enrolls it.

Chain-specific configuration is not hand-listed either. `DiscoverConfigPaths` reads every
`AZOA:`-rooted constant the assembly declares and fills it from the path's own last segment
(`*Retry*` ⇒ 0, `*Attempts` ⇒ 1, `*DelayMs` ⇒ 1, `*Mnemonic` and `*PrivateKey` ⇒ throwaway
platform material). A new provider that follows the existing naming convention is configured
without touching the harness.

## §the-transport-is-deliberately-dumb

`ConformanceTransport` has three modes and no chain knowledge: refuse everything, answer every
lookup with a clean miss, or answer HTTP 200 with `result: null` — the vacuous-null trap that
made `receipt?.Err == null` evaluate true for a receipt that does not exist. A per-chain stub
would let the battery drift into duplicating the pipeline suites; keeping it dumb is what forces
the battery to assert only things that are true of every rail.

The cost is that the value pipelines cannot reach signing without chain fixtures. That cost is
paid explicitly in the exemption ledger rather than hidden behind a skipped test.

## §exemptions-are-tested

`ProviderConformanceExemptions.All` is the certification ledger. Any (provider, invariant)
pairing absent from it MUST be certified directly by the battery. Three verdicts exist:

- `Certified` — the battery proves it here, against this provider.
- `CertifiedElsewhere` — the state needs chain-specific fixtures; the named suite carries the
  decisive evidence and the battery still asserts everything it can reach.
- `NotApplicable` — the provider structurally cannot exhibit the risk. Every one of these has a
  compensating control asserted by
  `Every_not_applicable_exemption_has_its_compensating_control_asserted`; a new `NotApplicable`
  verdict with no compensating control throws rather than passing.

Simulated is the only provider with `NotApplicable` verdicts, and each is paid for: every
identifier it returns carries the reserved `sim:` marker, it accepts no `HttpMessageHandler`, it
never emits the pending-confirmation marker, and it accepts no `ITransactionSignerFactory`.

`Certification_report` prints the whole matrix — verdict, reason and evidence per pairing — to
test output. That is the artefact to read when certifying a route.

## §shared-seams

Two invariants are properties of `BaseBlockchainProvider` rather than of any one chain, so
`ConformanceProbeProvider` (a subclass that adds no behaviour) certifies them once for everyone:
`RetrySafety.Broadcast` never re-invokes on an ambiguous post-send failure but does on a provably
pre-broadcast socket failure, and `NarrowToChainBaseUnits` refuses above the 64-bit ceiling rather
than handing back a wrapped count.

## §il-literal-scanning

`ILStringLiterals` reads the string literals compiled into a type and its generated async state
machines. It exists for one contract with no runtime surface: a provider that can broadcast must
carry `OperationStatus.PendingConfirmationMarker`, because a confirmation timeout is success with
a pending marker and never a failure. The scan is a deliberately naive `ldstr` walk gated on the
user-string token tag — it is a presence check, so a spurious extra literal is harmless.
