# tests/AZOA.WebAPI.IntegrationTests/Perf

## Why this directory exists at all

`SurrealPerfBudgets` is the only `[Trait("Category","Perf")]` class in the repo.
For its entire life it was excluded from every test filter
(`--filter "Category!=Chaos&Category!=Perf"`), so it had never executed anywhere.
It was kept because the operations it covers — the wallet single-record read, the
`bridge_tx` insert and the `saga_steps` due-scan — are the three store paths that
sit on a hot request path, and a store regression in any of them is invisible to
the functional suite, which asserts outcomes and never timing.

Keeping it only made sense alongside a job that runs it. That is the `perf` job in
`.github/workflows/ci.yml`. A perf suite with no runner is not coverage; it is a
file that looks like coverage.

## What is asserted, and what is only reported

Two different kinds of claim live in this class and they are deliberately not
given the same weight.

**Correctness is asserted hard.** Every measured operation must be proven to have
done its work:

- `SurrealWalletStore.UpsertAsync`/`GetByIdAsync` return `AZOAResult<T>` and
  capture exceptions rather than throwing. A store failing on every call returns
  in microseconds and passes any latency budget, so the seeded upserts and the
  measured reads are both checked.
- `AddBridgeAsync` and `EnqueueAsync` call `EnsureAllOk()` internally, so they
  throw on failure and need no post-hoc check.
- The due-scan asserts every measured iteration claimed a full batch. An empty
  scan is fast and meaningless; without this check a run against an unapplied
  schema passes trivially.

**Latency is reported, and only a regression ceiling is asserted.** The declared
targets (`50`/`100`/`200` ms p99) come from the original `surrealdb-migration`
track. They were never validated. The one measurement this repo has ever recorded
is in `conductor/tracks/integration-test-isolation-debt/INTEGRATION-TEST-PASSOFF.md`:

> `SurrealPerfBudgets.WalletGetById_P99_Under50ms` → machine-dependent timing
> (observed p99 64–132 ms vs 50 ms budget); flaky on this host.

So the 50 ms target is not merely unproven, it is known to be exceeded by ~2.6x on
an unloaded developer machine. A shared two-core CI runner with a containerised
database is slower, not faster. Asserting the declared targets would produce a job
that fails on noise, and a job that fails on noise gets excluded from CI — which is
the precise history that left this class dead.

The asserted value is therefore `declared target × CeilingMultiplier`, default
`8` (`400`/`800`/`1600` ms). The derivation: the only observed ratio of real to
declared is 2.6x on a quiet host; 8 is the smallest round multiple that clears
that with headroom for runner contention while still failing on an
order-of-magnitude regression, which is the class of regression a shared runner
can detect reliably. It is a deliberately loose bound, and it is honest about
being one. Tighten it when there is CI data to tighten it against: every run
prints `p50` and `p99` per operation to the test output, so the data accumulates
from the first green run.

Override with `AZOA_PERF_CEILING_MULTIPLIER` rather than editing the constant.

## The skip that must not be silent

Each test is `[SkippableFact]` and skips when `SurrealTestDefaults.Endpoint` does
not answer, so a developer without a container gets a skip rather than a failure.
In CI that same behaviour would make a vacuous green indistinguishable from a real
one in the test output.

`AZOA_PERF_REQUIRE_SURREAL=1` turns an unreachable server into a thrown
`InvalidOperationException` in `InitializeAsync`. The `perf` job sets it. The job
also curls `/health` from the host before running, so the failure is attributed to
the right step. Both controls are load-bearing: remove the environment variable and
the job can pass while running nothing.

## Schema bootstrap

Bootstrap goes through `SurrealTestSchema.BootstrapAsync`, the same helper the
store tests use, requesting the `wallet`, `bridge_tx` and `saga_steps` goldens.

The previous hand-rolled bootstrap in this file is what made two of the three tests
fail with `The namespace 'test<guid>' does not exist`: it sent a `Surreal-DB`
header naming a database that did not exist yet — faulting the connection-level
`USE` — and opened a fresh `HttpClient` for each of 61 schema statements, which
also contributed to the socket exhaustion recorded in the same triage document. The
shared helper applies the DDL as one batch on one connection with an in-batch
`USE DB`, and throws on a failed apply.

The connection is constructed only after the namespace and database exist. The old
ordering built it first, which is the other half of the same failure.

## Runner notes

The `perf` job publishes SurrealDB on `8020:8000`, matching
`SurrealTestDefaults.Endpoint` (`http://127.0.0.1:8020`), which is a compile-time
constant with no environment indirection. Changing the published port silently
breaks the suite into skips, which `AZOA_PERF_REQUIRE_SURREAL` then converts into a
visible failure.

Unlike the `dotnet` job's container, the perf container mounts no named volume: the
suite creates and drops its own per-run namespace and must not inherit state from a
previous run.
