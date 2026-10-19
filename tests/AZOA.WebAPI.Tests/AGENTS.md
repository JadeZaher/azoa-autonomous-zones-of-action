# Unit test harness notes

## §concurrent-start-gates

Concurrency tests use `TaskCompletionSource` with
`RunContinuationsAsynchronously` for shared start gates. Awaiting a gate parks a
contender without occupying a worker thread; a synchronous event wait inside
`Task.Run` can exhaust the worker pool while the test waits for every contender
to arrive, starving unrelated bounded race tests in the same process.

`BlockchainOperationManagerTests` waits asynchronously for all sixteen arrivals
before releasing their shared gate. The redeem/reverse bridge races retain their
existing contender counts and release order with asynchronous gates. Keep all
exactly-once, state and result assertions; do not compensate for harness blocking
by increasing business-test timeouts or serializing the suite.

The faucet's twelve identical-dispense contenders also wait for every arrival
asynchronously before release. A synchronous twelve-party barrier still starves
unrelated preparation/cancellation tests even after the sixteen-party gate is
fixed. Synchronous cancellation callbacks in bootstrap lifecycle tests are
intentional blocked-callback witnesses with bounded waits and a finally release;
completed in-memory store tasks do not create shared arrival barriers.

## runner-scheduling

Use xUnit core 2.9.3 and the Visual Studio runner 2.8.2 with the copied
`xunit.runner.json` selecting `conservative`. The earlier 2.4 runtime admits
awaiting tests aggressively, so resumed race/stop continuations compete with
unrelated CPU-heavy collections. Method-only full-suite snapshots showed context
workers executing BCrypt and source/crypto checks while unrelated bounded races
expired. Conservative scheduling retains collection parallelism and the default
worker count while bounding admitted test lifetimes; keep every existing race
timeout and contender count.

The core, assertion and analyzer packages are explicit so assertions remain
2.4.2 and analyzers remain 1.0.0. The removed analyzer test harness no longer
appears in resolved dependencies; its old assertion-constructor pin is not a
reason to retain the old execution runtime. Both core and runner must be 2.8+
for this algorithm. See [official parallel execution guidance](https://xunit.net/docs/running-tests-in-parallel)
and [runner configuration](https://xunit.net/docs/config-xunit-runner-json).
