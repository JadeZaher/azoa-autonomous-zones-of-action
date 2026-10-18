# Hosting — composition-root tests

`ProgramCompositionTests` builds the real Development host (`ValidateOnBuild` + `ValidateScopes`) and asserts the
`Program.cs` ordering rule (root `AGENTS.md` §Program.cs registration order): the KYC gate is `RiskHoldValueGate`
wrapping the merged KYC gate, `IWormholeAdapter` and the fungible creation lifecycle resolve, and the round-4
recovery workers are registered (`NodeFeeSettlementRecoveryWorker`, `HostedOutcomePollWorker`,
`TenantPayoutSweepWorker`, `WttStageKeyReadinessWorker`; RV-X X-9).

## Why host start is time-bounded

`ValidateOnBuild` cannot see through factory registrations (`AddScoped<IX>(sp => sp.GetRequiredService<Y>())`),
so a dependency cycle hidden behind one passes the build and recurses only when something resolves it. Program.cs's
post-build guards (`ValueRailStartup.GuardAccountPauseWired` resolves `IActionManager`) do exactly that during host
start. The recursion never reaches a `StackOverflowException`: DI's stack guard moves the deep resolution to a
fresh thread, which then blocks on the singleton/scoped lock the original thread holds — a silent deadlock, so the
host (and every `WebApplicationFactory` test, and the real API process) hangs instead of failing. `Services()` waits
90 s and fails with a pointer here instead of hanging the run. Found on round-4 L6:
`ActionManager → IEnumerable<IActionExternalOperationResolver> → DeploymentActionResolver → IDeploymentService →
DeploymentService → IFrozenActionRuntime → ActionManager` (`Program.cs:1419`, 439 nested frames in the hang dump).
Diagnose a future hang with `dotnet test --blame-hang-timeout 3m` and `dotnet-dump analyze <dmp> -c "clrstack -all"`.

Fixed in `r4/fix-core2`: `IFrozenActionRuntime` is `Managers/DeferredFrozenActionRuntime` (resolves `ActionManager` per
call, not at construction) — `Managers/AGENTS.md` §DeferredFrozenActionRuntime. When adding a "forward the interface to
the concrete scoped service" factory, check the concrete type's own constructor graph cannot reach the interface back.
