# WTT six-route local protocol fixture runner: brings up pinned Wormhole on Hardhat, a Solana test validator and
# AlgoKit LocalNet with ONE local guardian set, cross-registers token-bridge emitters, writes .state/ for
# WttSixRouteProtocolTests. -Down reaps exactly what -Up started. See AGENTS.md (this directory).
[CmdletBinding()]
param([switch]$Up, [switch]$Down, [int]$EvmPort = 8645, [int]$SolanaPort = 8999)
$ErrorActionPreference = 'Stop'
$fixture = $PSScriptRoot
$repository = (Resolve-Path (Join-Path $fixture '../../../..')).Path
$state = Join-Path $fixture '.state'
$solanaContainer = 'azoa-wtt-solana'
$algorandCompose = Join-Path $repository 'tests/AZOA.WebAPI.IntegrationTests/Fixtures/Algorand/compose.yml'
$pins = Get-Content (Join-Path $fixture 'wormhole-pins.json') -Raw | ConvertFrom-Json

function Invoke-Node([string[]]$Arguments) {
    $output = & node @Arguments
    if ($LASTEXITCODE -ne 0) { throw "node $($Arguments -join ' ') failed ($LASTEXITCODE)." }
    return $output
}

function Wait-Rpc([string]$Url, [string]$Body) {
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        try { Invoke-RestMethod -Uri $Url -Method Post -ContentType 'application/json' -Body $Body -TimeoutSec 2 | Out-Null; return }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw "RPC at $Url did not become ready."
}

if ($Down) {
    $pidFile = Join-Path $state 'evm.pid'
    if (Test-Path $pidFile) { Stop-Process -Id ([int](Get-Content $pidFile)) -Force -ErrorAction SilentlyContinue; Remove-Item $pidFile }
    podman rm -f $solanaContainer 2>$null | Out-Null
    podman compose -f $algorandCompose down 2>$null | Out-Null
    if (Test-Path $state) { Remove-Item -Recurse -Force $state }
    return
}
if (-not $Up) { throw 'Pass -Up or -Down.' }

Push-Location $fixture
try {
    npm ci --ignore-scripts --no-audit --no-fund | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed for the WTT fixture.' }
    Invoke-Node @('lib/verify-pins.mjs') | Select-Object -Last 1
    Invoke-Node @('solana/dump-programs.mjs') | Out-Null
    Invoke-Node @('lib/vaa.mjs', 'guardians', '--size', '1') | Out-Null

    # EVM: pinned core + token bridge on a loopback Hardhat node.
    $evmRpc = "http://127.0.0.1:$EvmPort"
    $hardhat = Start-Process -FilePath 'node' -PassThru -WindowStyle Hidden -WorkingDirectory (Join-Path $fixture 'evm') -ArgumentList @(
        (Join-Path $fixture 'node_modules/hardhat/internal/cli/bootstrap.js'), 'node', '--config', 'hardhat.config.cjs',
        '--hostname', '127.0.0.1', '--port', "$EvmPort")
    New-Item -ItemType Directory -Force $state | Out-Null
    Set-Content (Join-Path $state 'evm.pid') $hardhat.Id
    Wait-Rpc $evmRpc '{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}'
    Invoke-Node @('evm/chain.mjs', 'deploy', '--rpc', $evmRpc) | Out-Null

    # Solana: canonical program ids loaded from the pinned dumps, then initialized with the local guardian set.
    podman build -q -t azoa-wtt-solana -f (Join-Path $repository 'tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana/Containerfile') `
        (Join-Path $repository 'tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana') | Out-Null
    $programs = (Resolve-Path (Join-Path $fixture '.cache/solana')).Path
    podman run -d --name $solanaContainer -p "127.0.0.1:${SolanaPort}:8899" -p "127.0.0.1:$($SolanaPort + 1):8900" `
        -v "${programs}:/programs:ro" azoa-wtt-solana --reset --quiet `
        --bpf-program $pins.solana.programs.core.programId /programs/core.so `
        --bpf-program $pins.solana.programs.tokenBridge.programId /programs/tokenBridge.so | Out-Null
    $solanaRpc = "http://127.0.0.1:$SolanaPort"
    Wait-Rpc $solanaRpc '{"jsonrpc":"2.0","id":1,"method":"getHealth"}'
    Invoke-Node @('solana/initialize.mjs', '--rpc', $solanaRpc) | Out-Null

    # Algorand: pinned TEAL compiled and deployed on LocalNet by the upstream helpers in the pinned Python image.
    podman compose -f $algorandCompose up -d | Out-Null
    Wait-Rpc 'http://127.0.0.1:4001/health' '{}'
    $bootVaa = Invoke-Node @('lib/vaa.mjs', 'guardian-set-upgrade')
    $wormholeSources = Join-Path $state 'algorand-src'
    Copy-Item -Recurse -Force (Join-Path $fixture '.cache/upstream/algorand') $wormholeSources
    Copy-Item -Force (Join-Path $fixture 'algorand/*.py') $wormholeSources
    $python = "pip install --quiet --no-cache-dir -r requirements.txt && python deploy.py http://host.containers.internal:4001 http://host.containers.internal:4002 $bootVaa /state/algorand.json"
    podman run --rm -v "${wormholeSources}:/wormhole" -v "${state}:/state" -w /wormhole $pins.algorand.pythonImage sh -c $python | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Algorand Wormhole deployment failed.' }

    # Cross-register token-bridge emitters with locally signed governance VAAs.
    $emitters = @{
        2 = (Get-Content (Join-Path $state 'evm.json') -Raw | ConvertFrom-Json).tokenBridgeEmitterHex
        1 = (Get-Content (Join-Path $state 'solana.json') -Raw | ConvertFrom-Json).tokenBridgeEmitterHex
        8 = (Get-Content (Join-Path $state 'algorand.json') -Raw | ConvertFrom-Json).tokenBridgeEmitterHex
    }
    foreach ($foreign in 1, 8) {
        $vaa = Invoke-Node @('lib/vaa.mjs', 'register-chain', '--chain', "$foreign", '--emitter', $emitters[$foreign])
        Invoke-Node @('evm/chain.mjs', 'register', '--vaa', $vaa) | Out-Null
    }
    foreach ($foreign in 2, 1) {
        $vaa = Invoke-Node @('lib/vaa.mjs', 'register-chain', '--chain', "$foreign", '--emitter', $emitters[$foreign])
        podman run --rm -v "${wormholeSources}:/wormhole" -v "${state}:/state" -w /wormhole $pins.algorand.pythonImage `
            sh -c "pip install --quiet --no-cache-dir -r requirements.txt && python register.py /state/algorand.json $vaa" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Algorand registration of chain $foreign failed." }
    }
    # Solana RegisterChain (verify_signatures + post_vaa + register_chain) is not authored: deferred.md WTT-SIX-ROUTE-SOLANA-REGISTRATION.
    Set-Content (Join-Path $state 'wtt-local.json') (@{ ready = $true; commit = $pins.upstream.commit } | ConvertTo-Json)
    Write-Host "WTT fixtures up: EVM $evmRpc, Solana $solanaRpc, Algorand http://127.0.0.1:4001. Reap with -Down."
}
finally { Pop-Location }
