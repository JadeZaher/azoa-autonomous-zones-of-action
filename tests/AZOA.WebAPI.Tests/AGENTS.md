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
