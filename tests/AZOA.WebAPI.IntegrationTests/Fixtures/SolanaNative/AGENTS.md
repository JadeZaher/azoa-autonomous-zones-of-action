# Native Solana protocol fixture

This is a separate build artifact for the local validator described in
`../Solana/AGENTS.md`. It does not replace that validator or reset its ledger.
The fixed Rust program has no package dependencies. Two features produce distinct
ELF revisions which both return success; this lets the real adapter prove which
program revision executed, rather than infer it from a successful receipt.

The builder uses Rust 1.89.0, the checksum-verified Agave v3.1.8 archive already
used by the validator fixture, and platform-tools v1.52 targeting SBPF v0.
These compiler defaults and flags come from the pinned official sources:
https://github.com/anza-xyz/agave/blob/v3.1.8/platform-tools-sdk/cargo-build-sbf/src/toolchain.rs
and https://github.com/anza-xyz/agave/blob/v3.1.8/platform-tools-sdk/cargo-build-sbf/src/main.rs.
Captures are in `.omc/research/native-sbf-*-2026-10-15.md`.

Run only in the final integrated sweep:

```powershell
podman build -t localhost/azoa-native-elf:agave3.1.8 -f tests/AZOA.WebAPI.IntegrationTests/Fixtures/SolanaNative/Containerfile tests/AZOA.WebAPI.IntegrationTests/Fixtures/SolanaNative
podman create --name azoa-native-elf-export localhost/azoa-native-elf:agave3.1.8 /out/one/azoa_native_fixture.so
podman cp azoa-native-elf-export:/out .omc/native-elf
$env:AZOA_SOLANA_NATIVE_ELF_ONE = (Resolve-Path .omc/native-elf/one/azoa_native_fixture.so).Path
$env:AZOA_SOLANA_NATIVE_ELF_TWO = (Resolve-Path .omc/native-elf/two/azoa_native_fixture.so).Path
$env:AZOA_SOLANA_VALIDATOR_URL = 'http://127.0.0.1:18899'
$env:AZOA_SOLANA_LOCAL_GENESIS = (Invoke-RestMethod -Uri $env:AZOA_SOLANA_VALIDATOR_URL -Method Post -ContentType application/json -Body '{"jsonrpc":"2.0","id":1,"method":"getGenesisHash","params":[]}').result
```

Inspect existing container names before creating anything; reuse the export
container if it already holds this image. The export container is never started.
Record the ELF SHA256 manifest, compiler image ID, validator genesis, and cloned
Metaplex program hash with gate evidence. The expected genesis is set by setup
before the test; the fixture independently checks it before its local faucet.

`SolanaNativeProtocolTests` uses the production provider, real SurrealDB preparation
journal and exact prepared bytes across provider reconstruction. It deploys/calls
both ELF revisions, removes upgrade authority, checks prior-call history and rejects
changed-code proof, then exercises real mint setup, ATA creation, mutable metadata,
immutable metadata refusal and empty-account rent return. Metadata tests require
the Metaplex program loaded by the existing validator setup. Every RPC endpoint
must be an explicit HTTP loopback IP. Missing setup fails the gate; it is not skipped.
No build, validator launch, funding or test execution has occurred during authoring.
