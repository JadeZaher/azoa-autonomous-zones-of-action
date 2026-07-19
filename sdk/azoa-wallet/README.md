# @azoa/sdk

Cross-platform TypeScript SDK for AZOA — a typed HTTP client + fluent facades
over the AZOA WebAPI, built for **browser / React Native / Lynx**. Client-side
signing, multi-chain wallet, DEX integration, and a durable **workflow** run
driver. No `btoa`/`atob`/`Buffer`; pure-JS encoding only.

```ts
import { AzoaClient } from "@azoa/sdk";

const azoa = new AzoaClient({
  apiUrl: "https://api.azoa.example",
  apiKey: "azoa_pk_…", // server-to-server (tenant) auth
});
```

All public methods return a `Result<T, SdkError>` discriminated union and never
throw — except the synchronous input-guard throw (`assertUuid` / non-empty
string) that fires before any network call. Check the result with `isOk` /
`isErr`:

```ts
import { isOk } from "@azoa/sdk";

const res = await azoa.workflow.templates /* … */;
if (isOk(res)) { /* res.value */ } else { /* res.error: SdkError */ }
```

Bridge base-unit amounts are decimal strings in both request and response
contracts. This preserves every value through `ulong.MaxValue` without crossing
JavaScript's unsafe-number boundary. Tenant custody capabilities and KYC session
responses also expose `developmentSimulation`; integrations should display that
state and must not present simulated verification as production KYC.

---

## Workflow SDK (`azoa.workflow`)

DESIGN a workflow *shape* once as a `QuestTemplate` (a DAG of generic nodes with
`{{param}}` slots), then DRIVE many actors through **durable, step-addressable**
runs of that shape. The economic / token domain stays in the consumer app; this
SDK exposes **mechanism-only** primitives — no economic semantics.

### Template authoring (DESIGN once)

```ts
// Create a reusable shape
const created = await azoa.workflow.createTemplate({
  name: "onboarding",
  nodes: [/* … */],
  edges: [/* … */],
  parameters: JSON.stringify({ amount: "1000" }),
});

// Read templates
const tpl  = await azoa.workflow.getTemplate(templateId);
const all  = await azoa.workflow.listTemplates();

// Instantiate a quest from a template with {{param}} values
const quest = await azoa.workflow.instantiate(templateId, { amount: "1000" });
```

### The fluent `quest()` run driver (DRIVE)

`azoa.workflow.quest(...)` opens a **run handle** whose chainable methods map
1:1 onto the durable-workflow-engine advancement endpoints. The handle is
**awaitable (thenable)**: the chain runs in order and resolves to the final
`Result<WorkflowRunResult, SdkError>`. The first error **short-circuits** the
rest of the chain (no throw).

```ts
// Phase-by-phase drive — quest(step1).step(step2B)
const result = await azoa.workflow
  .quest(questId)
  .start({ params: { amount: "1000" } })
  .step("11111111-1111-1111-1111-111111111111")   // POST runs/{runId}/advance {fromNodeId}
  .step("22222222-2222-2222-2222-222222222222");

if (isOk(result)) {
  console.log(result.value.status); // e.g. "Running" | "Suspended" | "Succeeded"
}
```

Three explicit entrypoints disambiguate what you're driving (no id-shape
sniffing):

```ts
azoa.workflow.quest(questId);                 // existing quest → start-workflow
azoa.workflow.quest.fromTemplate(templateId); // instantiate, then start-workflow
azoa.workflow.quest.run(runId);               // re-attach to a prior run
```

### Hybrid: start-and-signal-at-gates

A run can be driven **phase-by-phase** (`.step()`) OR
**start-and-signal-at-gates** (`.start()` then `.signal()`); both compose on the
same handle.

```ts
const handle = azoa.workflow.quest(questId);
await handle.start({ params });
// … run auto-advances and parks at a gate …
await handle.signal("gate-phase-1", "phase-met"); // POST runs/{runId}/signal {gateId, payload}
```

> **`gateId` is a free string**, not a UUID — it is guarded as a non-empty
> string. `payload` is a string (or `null`).

### Suspend awareness + status

```ts
azoa.workflow
  .quest(questId)
  .onSuspend((run) => {
    // fires when a call leaves the run Suspended / AwaitingSignal / AwaitingTimer
    console.log("parked:", run.status, "run", run.id);
  })
  .start({ params });

// Explicit poll for long-parked runs (GET /api/quest/runs/{runId})
const status = await azoa.workflow.quest.run(runId).status();
```

`WorkflowRunStatus` mirrors the engine 1:1: `Pending`, `Running`, `Succeeded`,
`Failed`, `Forked`, `Cancelled`, `Suspended`, `AwaitingSignal`, `AwaitingTimer`.
Helpers `isAwaiting(status)` / `isTerminal(status)` are exported.

### Idempotency on value-moving advances

`.step()` and `.signal()` take an optional `{ idempotencyKey }` that sets the
`Idempotency-Key` header (same plumbing as `executeSwap`). When absent the server
falls back to its deterministic content key.

```ts
await azoa.workflow
  .quest(questId)
  .start({ params })
  .step(nodeId, { idempotencyKey: crypto.randomUUID() });
```

### Acting FOR a child avatar — `forActor`

A tenant principal (authed by `X-Api-Key`) can drive a run **as** one of its
child avatars. `forActor` takes a plain avatar id; on the first advancement call
the SDK lazily acquires a short-lived child credential
(`POST /api/tenant/avatars/{childAvatarId}/credential`, with the tenant
`X-Api-Key` as the principal), caches the child JWT for the handle's lifetime,
re-acquires it on expiry, and threads it as a per-run `Authorization: Bearer`
override on the advance/signal calls — leaving the global tenant `X-Api-Key`
untouched.

```ts
await azoa.workflow
  .quest.fromTemplate(templateId)
  .forActor(childAvatarId)        // or .start({ actor: childAvatarId })
  .start({ params: { amount: "1000" } })
  .step(nodeId);
```

When no `forActor` (or `actor`) is set, the run uses the active session token /
API key unchanged.

### Typed `nodeConfig` builders

Pure helpers that serialize the **generic mechanism** params of each node type
into the `Config` JSON string a template/quest node carries. They type only
generic params — amounts are **strings**, and there is no rate, token meaning, or
economic concept. A raw `Config` is always accepted as an escape hatch.

```ts
import { nodeConfig } from "@azoa/sdk";

nodeConfig.gateCheck({ predicate: "upstream.swap.outAmount >= reads.threshold",
                       reads: { threshold: "1000" } });
nodeConfig.emit({ payload: { event: "granted" } });
nodeConfig.swap({ request: { chain: "algorand", quoteId, walletAddress } });
nodeConfig.grant({ request: { walletId, name: "Reward", chainId: "algorand" },
                   holonId });
nodeConfig.transfer({ nftId, request: { targetAvatarId, walletId } });
nodeConfig.refund({ nftId, request: { targetAvatarId, walletId } });

// Escape hatch for un-typed node kinds:
nodeConfig.raw({ anything: "goes", amount: "100" });
```

### Worked illustration (non-normative)

A reward flow might compose `swap → gateCheck → grant`: swap inputs at the DEX
rate, gate on a phase predicate over the swap's output, then grant an asset to
the actor. The economic meaning of those amounts lives in the **consumer app** —
this SDK only wires the generic mechanism.

---

## Path constants

The workflow run-driver routes are exported on `API_PATHS` (from
`@azoa/sdk/api`):

| Constant | Route |
| --- | --- |
| `QUEST_START_WORKFLOW(questId)` | `POST /api/quest/{questId}/start-workflow` |
| `QUEST_RUN_ADVANCE(runId)` | `POST /api/quest/runs/{runId}/advance` |
| `QUEST_RUN_SIGNAL(runId)` | `POST /api/quest/runs/{runId}/signal` |
| `QUEST_RUN_STATUS(runId)` | `GET /api/quest/runs/{runId}` |
| `QUEST_RUN_EXECUTION_STATE(runId)` | `GET /api/quest/runs/{runId}/execution-state` |
| `TENANT_CHILD_CREDENTIAL(avatarId)` | `POST /api/tenant/avatars/{avatarId}/credential` |
