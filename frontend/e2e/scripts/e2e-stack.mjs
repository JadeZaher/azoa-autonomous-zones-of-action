#!/usr/bin/env node
// Runs the self-contained E2E compose project (`azoa-e2e`): fake vendors, per-run secrets, test CA.
// Usage: node e2e/scripts/e2e-stack.mjs <prepare|up|logs|down> [--rotate] [--wipe]
// See frontend/e2e/AGENTS.md §stack.
import { spawnSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'

const projectName = 'azoa-e2e'
const composeFile = 'docker-compose.e2e.yml'
const e2eRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repositoryRoot = path.resolve(e2eRoot, '..', '..')
const generated = path.join(e2eRoot, '.generated')
const trustDirectory = path.join(generated, 'trust')
const environmentFile = path.join(generated, 'e2e.env')
const probeSource = path.join(e2eRoot, 'stack', 'egress-probe')
const probeOutput = path.join(generated, 'egress-probe')
const sdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0'
const certificateLifetimeDays = 7

const command = process.argv[2]
const rotate = process.argv.includes('--rotate')
const wipe = process.argv.includes('--wipe')

function fail(message) {
  console.error(`[e2e-stack] ${message}`)
  process.exit(1)
}

function run(executable, args, options = {}) {
  const result = spawnSync(executable, args, { stdio: 'inherit', ...options })
  if (result.error) fail(`${executable} could not start: ${result.error.message}`)
  if (result.status !== 0) fail(`${executable} ${args.join(' ')} exited with ${result.status}`)
  return result
}

function findOpenssl() {
  for (const candidate of ['openssl', 'C:\\Program Files\\Git\\usr\\bin\\openssl.exe']) {
    const probe = spawnSync(candidate, ['version'], { stdio: 'ignore' })
    if (!probe.error && probe.status === 0) return candidate
  }
  return fail('openssl is required to mint the per-run test CA (install it or put Git for Windows usr/bin on PATH).')
}

/**
 * A throwaway CA plus one server certificate for every name the API container may dial, and a
 * trust directory holding ONLY that CA (as `ca.pem` and its OpenSSL `<subject-hash>.0` name).
 */
function ensureCertificates() {
  const certificate = path.join(generated, 'vendor.pem')
  const fresh =
    existsSync(certificate) &&
    existsSync(trustDirectory) &&
    Date.now() - statSync(certificate).mtimeMs < (certificateLifetimeDays - 1) * 24 * 60 * 60 * 1000
  if (fresh && !rotate) return

  const openssl = findOpenssl()
  // MSYS openssl must see "/CN=..." literally, never as a POSIX path to convert.
  const options = { cwd: generated, env: { ...process.env, MSYS_NO_PATHCONV: '1', MSYS2_ARG_CONV_EXCL: '*' } }
  writeFileSync(
    path.join(generated, 'vendor.ext'),
    [
      'basicConstraints=CA:FALSE',
      'extendedKeyUsage=serverAuth',
      'subjectAltName=DNS:host.docker.internal,DNS:host.containers.internal,DNS:localhost,DNS:webhook-receiver.e2e.azoa.test,IP:127.0.0.1',
      '',
    ].join('\n'),
  )
  run(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', String(certificateLifetimeDays),
    '-subj', '/CN=AZOA E2E throwaway CA', '-keyout', 'ca-key.pem', '-out', 'ca.pem',
    '-addext', 'basicConstraints=critical,CA:TRUE', '-addext', 'keyUsage=critical,keyCertSign,cRLSign'], options)
  run(openssl, ['req', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=azoa-e2e-vendors',
    '-keyout', 'vendor-key.pem', '-out', 'vendor.csr'], options)
  run(openssl, ['x509', '-req', '-in', 'vendor.csr', '-CA', 'ca.pem', '-CAkey', 'ca-key.pem', '-CAcreateserial',
    '-days', String(certificateLifetimeDays), '-out', 'vendor.pem', '-extfile', 'vendor.ext'], options)

  rmSync(trustDirectory, { recursive: true, force: true })
  mkdirSync(trustDirectory, { recursive: true })
  const subjectHash = run(openssl, ['x509', '-hash', '-noout', '-in', 'ca.pem'], { ...options, stdio: 'pipe', encoding: 'utf8' })
    .stdout.trim()
  copyFileSync(path.join(generated, 'ca.pem'), path.join(trustDirectory, 'ca.pem'))
  copyFileSync(path.join(generated, 'ca.pem'), path.join(trustDirectory, `${subjectHash}.0`))
}

/** Random per-run credentials, shared by the API container and the Playwright run. Returns true when written. */
function ensureSecrets() {
  if (existsSync(environmentFile) && !rotate) return false
  const secret = () => randomBytes(24).toString('hex')
  const values = {
    AZOA_E2E_IDENTOMAT_COMPANY_KEY: secret(),
    AZOA_E2E_IDENTOMAT_CALLBACK_KEY: secret(),
    AZOA_E2E_IDENTOMAT_KYC_CONFIG: `kyc-config-${secret().slice(0, 12)}`,
    AZOA_E2E_IDENTOMAT_KYB_CONFIG: `kyb-config-${secret().slice(0, 12)}`,
    AZOA_E2E_PERSON_KEY_SECRET: secret(),
    AZOA_E2E_STRIPE_SECRET_KEY: secret(),
    AZOA_E2E_STRIPE_WEBHOOK_SECRET: secret(),
    AZOA_E2E_OPERATOR_PASSWORD: secret(),
  }
  writeFileSync(environmentFile, Object.entries(values).map(([key, value]) => `${key}=${value}`).join('\n') + '\n')
  return true
}

function readEnvironmentFile() {
  return Object.fromEntries(
    readFileSync(environmentFile, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  )
}

function composeCommand() {
  const configured = process.env.AZOA_E2E_COMPOSE
  if (configured) {
    const [executable, ...preArgs] = configured.split(' ')
    return { executable, preArgs }
  }
  for (const [executable, ...preArgs] of [['docker', 'compose'], ['podman', 'compose']]) {
    const probe = spawnSync(executable, [...preArgs, 'version'], { stdio: 'ignore' })
    if (!probe.error && probe.status === 0) return { executable, preArgs }
  }
  return fail('No compose runtime found (docker compose or podman compose).')
}

/** The engine CLI (`docker` or `podman`) behind the compose command. */
function containerEngine() {
  return process.env.AZOA_E2E_CONTAINER_RUNTIME ?? (composeCommand().executable.includes('podman') ? 'podman' : 'docker')
}

function compose(args, environment, options = {}) {
  const { executable, preArgs } = composeCommand()
  return run(executable, [...preArgs, '-p', projectName, '-f', composeFile, ...args], {
    cwd: repositoryRoot,
    env: environment,
    ...options,
  })
}

/** Volumes are only ever removed for THIS project (its file declares `name: azoa-e2e`). */
function removeStack(environment, withVolumes) {
  if (withVolumes) {
    const declared = /^name:\s*(\S+)/m.exec(readFileSync(path.join(repositoryRoot, composeFile), 'utf8'))?.[1]
    if (declared !== projectName) fail(`Refusing to remove volumes: ${composeFile} declares project '${declared}'.`)
  }
  compose(['down', '--remove-orphans', ...(withVolumes ? ['-v'] : [])], environment)
}

/**
 * Builds the BCL-only egress probe in an SDK container (the API image has no SDK). The source is
 * `.csx` so the repo-root AZOA.WebAPI.csproj never compiles it; it becomes Program.cs only inside
 * the build container.
 */
function ensureEgressProbe() {
  const dll = path.join(probeOutput, 'AzoaEgressProbe.dll')
  const newestSource = Math.max(
    statSync(path.join(probeSource, 'EgressProbe.csx')).mtimeMs,
    statSync(path.join(probeSource, 'AzoaEgressProbe.csproj')).mtimeMs,
  )
  if (existsSync(dll) && statSync(dll).mtimeMs > newestSource) return
  rmSync(probeOutput, { recursive: true, force: true })
  mkdirSync(probeOutput, { recursive: true })
  const user = typeof process.getuid === 'function' ? ['--user', `${process.getuid()}:${process.getgid()}`] : []
  run(containerEngine(), [
    'run', '--rm', ...user,
    '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp', '-e', 'DOTNET_NOLOGO=1', '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '--mount', `type=bind,source=${probeSource},target=/src,readonly`,
    '--mount', `type=bind,source=${probeOutput},target=/out`,
    sdkImage, 'sh', '-c',
    'mkdir -p /tmp/build && cp /src/AzoaEgressProbe.csproj /tmp/build/ && cp /src/EgressProbe.csx /tmp/build/Program.cs '
      + '&& dotnet publish /tmp/build/AzoaEgressProbe.csproj -c Release -o /out --nologo',
  ])
}

async function waitFor(url, label) {
  const deadline = Date.now() + 8 * 60 * 1000
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url)
      if (response.ok) return
    } catch {
      // not listening yet
    }
    await new Promise((resolve) => setTimeout(resolve, 3000))
  }
  fail(`${label} did not become healthy at ${url}.`)
}

/**
 * `process.env` wins over the generated file, the same precedence e2e/support/environment.ts uses.
 * The E2E ports are dedicated (3200/5200) and never read AZOA_FRONTEND_HOST_PORT, which the dev
 * stack uses to coexist with another frontend.
 */
function stackEnvironment() {
  const revision = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8' }).stdout.trim()
  const settings = { ...readEnvironmentFile(), ...process.env }
  return {
    SOURCE_REVISION: revision,
    AZOA_E2E_FRONTEND_PORT: '3200',
    AZOA_E2E_API_PORT: '5200',
    ...settings,
    AZOA_E2E_IDENTOMAT_UPSTREAM_PORT: settings.AZOA_E2E_IDENTOMAT_UPSTREAM_PORT ?? settings.AZOA_E2E_IDENTOMAT_PORT ?? '9443',
    AZOA_E2E_STRIPE_UPSTREAM_PORT: settings.AZOA_E2E_STRIPE_UPSTREAM_PORT ?? settings.AZOA_E2E_STRIPE_PORT ?? '9444',
  }
}

mkdirSync(generated, { recursive: true })

if (command === 'prepare' || command === 'up') {
  ensureCertificates()
  if (ensureSecrets()) {
    // The operator credential is seeded once per volume: a kept volume would keep the OLD password
    // and every operator login would answer 401. New secrets therefore always start a fresh volume.
    console.log(`[e2e-stack] new secrets written; removing any existing ${projectName} stack and its volumes so the operator is re-seeded.`)
    removeStack(stackEnvironment(), true)
  }
  ensureEgressProbe()
  console.log(`[e2e-stack] certificates, secrets and egress probe ready in ${generated}`)
}

if (command === 'up') {
  const environment = stackEnvironment()
  compose(['up', '-d', '--build'], environment)
  await waitFor(`http://localhost:${environment.AZOA_E2E_API_PORT}/health`, 'The API')
  await waitFor(`http://localhost:${environment.AZOA_E2E_FRONTEND_PORT}/login`, 'The frontend')
  console.log(`[e2e-stack] project ${projectName} is up; run \`npm run test:e2e\` from frontend/.`)
} else if (command === 'logs') {
  // Written next to the Playwright output so CI uploads both together.
  const output = path.join(e2eRoot, '.output')
  mkdirSync(output, { recursive: true })
  const logs = compose(['logs', '--no-color'], stackEnvironment(), { stdio: 'pipe', encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 })
  writeFileSync(path.join(output, 'stack.log'), `${logs.stdout ?? ''}${logs.stderr ?? ''}`)
  console.log(`[e2e-stack] compose logs written to ${path.join(output, 'stack.log')}`)
} else if (command === 'down') {
  // The E2E volume survives by default so local reruns skip schema application; --wipe (or CI)
  // drops it. The dev stack is a different project and is never touched either way.
  removeStack(stackEnvironment(), wipe || process.env.CI === 'true')
  console.log(`[e2e-stack] project ${projectName} removed${wipe || process.env.CI === 'true' ? ' with its volumes' : ''}.`)
} else if (command !== 'prepare') {
  fail('Usage: node e2e/scripts/e2e-stack.mjs <prepare|up|logs|down> [--rotate] [--wipe]')
}
