---
type: implementation-handoff
---

# Consent delivery source handoff

Status: bounded store source reviewed; worker/public entry point cutover authored and registered, independent integration review pending, runtime unverified.

Every consent lifecycle now creates immutable transition evidence including original receipt, body hash and original destination. Granted evidence commits with its command receipt; revoke, expiry and offboard use the same notification evidence builder. Delivery validates all immutable identity/body/time/destination fields before obtaining a lease. Legacy notifications without evidence cannot send or retry.

The new consent delivery store implements bounded candidate discovery, exact epoch/current registration and secret lease claims, database-time expiry, attempt consumption before HTTP, exact-holder completion and unleased unavailable-event suspension. Management lists metadata only and permits an audited retry of the exact dead-letter epoch under actual tenant or recent operator authority. Consent retry command hashes use a separate domain from action retries. Retries preserve lifetime counts and original destination.

Pure evidence fixtures are authored but unrun. Required next: real Surreal lease/concurrency/retry fixtures, shared-sender worker replacement, actual lifecycle caller cutover, discovery/retry endpoints and SDK/UI, independent review, generated schema and final integrated sweep. These are implementation obligations, not deferrals.

Worker and public user grant/revoke plus tenant offboard entry points now use atomic lifecycle commands. Tenant/operator consent event management endpoints are registered. Internal provisioning and account-claim migration, legacy mutation removal, SDK/UI and final verification remain required. New real-database lease/retry fixtures and actual worker transport fixtures are authored, unrun.
