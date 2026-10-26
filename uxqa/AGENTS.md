---
type: Agent Instructions
title: UX QA study implementation
---

# UX QA implementation

The study uses the local `ux-qa-research` plugin without editing either installed copy. Set `UXQA_PLUGIN_LIB` to its `lib` directory. The source and Claude cache were identical across 42 source files on 2026-10-18. The study is prepared; preparation is not evidence of an executed pass.

## Adapter guards

`adapter/adapter.mjs` allows only the dedicated E2E frontend/API ports (3200/5200 by default), exact loopback HTTP origins, named `azoa-e2e` containers, the Development API environment, and fake vendor origins. Container port bindings must connect those origins to the expected containers. It checks health independently and repeats the in-target checks before each account registration. The default runtime is Podman; `UXQA_CONTAINER_RUNTIME=docker` selects Docker. This is read-only container inspection, not deployment.

The adapter creates ordinary accounts through the real registration endpoint and obtains storage state through the real login page. Credentials persist only in `.secrets/accounts.json` so repeated seeding reuses the same marked account. Registration intent is persisted before the write; a login probe recovers a prior successful registration after a lost response without creating a second account. Only `qa-uxqa-*` handles are accepted. The E2E operator is the stack's pre-existing disposable fixture, not a production identity; only the specifically declared operator persona receives its session. Its password is read from the ignored E2E generated file and never printed. Set `UXQA_E2E_ROOT` to a worktree's `frontend/e2e` path when that checkout built the stack. The fixture path must remain inside this workspace. No root key is required or accepted by this adapter.

If a partially created account cannot log in, the adapter refuses with a generic status. The coordinator investigates locally; it must not silently rotate credentials, reset the database, or substitute production credentials.

## Execution boundaries

Keep UI-only lanes free of source inspection and API calls. Gold task success comes from the plugin's predicates. Probe evidence is separate and cannot increase UI gold-task success. Never publish traces, sessions, account files, or raw provider responses. Every journey must call `finish()` in `finally`, including failed journeys. All application fixes remain outside this directory and require independent review and the coordinator's integrated final verification sweep.

## Prepared coordinator tenant fixture

`adapter/prepare-tenant-designer.mjs` is a separate coordinator tool. It refuses without an explicit scenario flag, repeats the ordinary adapter's in-target/loopback guard before each privilege write, accepts only the fixed saved `qa-uxqa-ai-tenant` identity, and verifies its self profile through the real API. It assigns developer only to an ordinary user, never grants manager/admin, and ensures one active one-day marked tenant credential through the existing operator route. Raw tenant keys are discarded because designer admission needs only the live stored provisioning authority. The receipt contains identifiers/status, not credentials. Existing manager/non-study identities, ambiguous keys, missing fixture credentials and policy refusals remain failures. No key is rotated or revoked to hide a failure. The coordinator executed the guarded fixture and refreshed its real UI login before pass 03; that pass consumed two synthetic interview requests and remains a recorded failure.

The script does not call the model, launch a browser, change infrastructure or configure provider secrets. Re-seeding the ordinary persona through the standard adapter after promotion refreshes browser login claims. Preparation code and syntax checking are not authorization to run this privilege step; the root coordinator must first approve the narrow scenario, prove the guards and set/remove its one-command process-local flag.
