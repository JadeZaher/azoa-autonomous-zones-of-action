---
type: UX Lane Report
title: Account journey at 200 percent zoom and slow network
---

Outcome: COMPLETED. qa-uxqa-zoom reached overview, wallets, identity status and settings with no failed attempt, workaround or abandonment. This is an inspection journey: no wallet creation, verification start, value operation, provider save, notification or permission change was attempted.

Constraints: patience 2; plugin zoom=2 models 200 percent through 683 by 450 CSS viewport at deviceScaleFactor 2 (nominal 1366 by 900); slow=true applies 400 ms network latency, download 200000 bytes/second, upload 100000 bytes/second. This is the plugin model, not a native browser zoom-control test.

Private evidence: runs/qa-uxqa-zoom--zoom-slow-account/trace.zip, ux.json, overview.png, navigation-open.png, wallets.png, verify-identity.png, settings.png. Screenshots capture the viewport/scroll-container state; the full settings text was read from visible UI text and the accessibility tree. Replay: flows/zoom-slow-account/replay.mjs (syntax checked, not re-executed).

Observed facts:

- Overview showed authenticated account status, followed by two duplicate red messages: Chain info failed: TypeError: Failed to fetch. No user-facing retry or explanation appeared in the observed main content. The lane continued through navigation. Infrastructure cause remains unconfirmed; no external endpoint was probed.
- The hamburger button is visually recognizable but has an empty accessibility name. Axe reported critical button-name with 1 node on all four inspected pages. The accessibility snapshot shows a nameless banner button. Pointer activation opened a labelled Navigation dialog with visible links, so this zoom persona recovered unaided.
- Wallets showed zero wallets and no balances with connect/create actions. The empty state was understandable; actions were left untouched.
- Verify identity explained that funding/cashing out need verification and documents go to the provider. It said verification had not started. Start verification was enabled and Refresh result disabled. No reason for disabled refresh was separately stated in the observed copy; the not-started context allows an inference, not proof that users understand it.
- Settings was reachable from the Navigation dialog. It exposed session status, webhook registration, tenant permissions, deliveries, SDK configuration, provider override and session controls. The long settings view required vertical scrolling. No save or session control was activated. Raw session was displayed with a masked token; no session file was read.
- Axe detections: Overview button-name critical 1 node, color-contrast serious 2 nodes, scrollable-region-focusable serious 1 node; Wallets, Verify identity and Settings each button-name critical 1 node and color-contrast serious 1 node. These are rule-route detections, not deduplicated defects.

Expected/actual/recovery: a returning developer should retain account context while navigating with enlarged text and slow responses. Account identity stayed in the header and each screen had a clear heading. All targeted destinations were reached through the visible hamburger dialog. No observed navigation failure. The duplicate raw chain errors reduce comprehension of system state; the lane did not establish why requests failed or whether any wallet function is operational.

Finished metrics: 87 seconds; 6 clicks; 0 dead/rage clicks; 0 inputs; 0 validation errors; 5 navigation events, 4 distinct pages, 1 revisit; 0 HTTP errors, 3 console errors, 0 failed attempts, 0 workarounds; axeViolations 9, friction 15; 0 API operations matched by configured harness prefixes. Zero matched operations does not mean the application made no network requests.

Executed commands:

1. Required adapter guard with host access immediately before lane: OK azoa-e2e Development fake-vendors loopback-bindings, exit 0.
2. node uxqa/passes/01-release-readiness/flows/zoom-slow-account/interactive.mjs with TTY: 6 saved UI command blocks then FINISH; finally called finish and wrote trace plus metrics.
3. node --check uxqa/passes/01-release-readiness/flows/zoom-slow-account/replay.mjs: exit 0 (syntax only).

Guard environment: UXQA_PLUGIN_LIB=C:/Users/atooz/.codex/plugins/cache/local/ux-qa-research/0.1.0/lib; UXQA_CONTAINER_RUNTIME=podman; UXQA_E2E_ROOT=C:/Users/atooz/Programming/Projects/oasis-sleek/.worktrees/node-admin-release/frontend/e2e; command node uxqa/adapter/adapter.mjs guard.

Harness setup attempt before the first lane: importing ux-lib through node_repl returned process is not defined before any uxSession was created. It was replaced with the saved Node/TTY harness; no UI journey is claimed for that failed import.

Research hypotheses: raw duplicate chain failures may suggest total service outage to a returning member; the nameless menu creates an accessibility barrier despite success for this pointer persona; the settings density may slow locating safe account-only actions. Validate with five returning developers using 200 percent zoom and slow connections, including screen-reader users, and ask them to explain service status, locate settings and distinguish read-only information from configuration actions. No human success rate is claimed from this single agent run.
