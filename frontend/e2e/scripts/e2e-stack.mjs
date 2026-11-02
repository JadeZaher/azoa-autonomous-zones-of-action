#!/usr/bin/env node
// Runs the self-contained E2E compose project (`azoa-e2e`): fake vendors, per-run secrets, test CA.
// Usage: node e2e/scripts/e2e-stack.mjs <prepare|up|logs|down> [--rotate] [--wipe]
// See frontend/e2e/AGENTS.md §stack.
import { spawnSync } from 'node:child_process'
import { createHash, randomBytes } from 'node:crypto'
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'

const projectName = process.env.AZOA_E2E_PROJECT ?? 'azoa-e2e'
if (!/^azoa-(?:payment-)?e2e(?:-[a-z0-9][a-z0-9-]{0,39})?$/.test(projectName)) throw new Error('Use a bounded AZOA E2E project name.')
const composeFile = 'docker-compose.e2e.yml'
const e2eRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repositoryRoot = path.resolve(e2eRoot, '..', '..')
const generatedSubdir = projectName === 'azoa-e2e' ? '.' : projectName
const generated = path.join(e2eRoot, '.generated', generatedSubdir)
const ownershipFile = path.join(generated, 'owner-id')
const apiDockerfile = path.join(generated, 'api.Dockerfile')
let ownerId = ''
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

/** Refuses to adopt or remove any resource outside this generated run's ownership. */
function establishOwnership() {
  const engine = containerEngine()
  const expected = existsSync(ownershipFile) ? readFileSync(ownershipFile, 'utf8').trim() : null
  if (expected !== null && !/^[a-f0-9]{32}$/.test(expected)) fail('The E2E ownership marker is invalid.')
  const resources = [
    ...['surrealdb', 'api', 'frontend', 'webhook-receiver'].map(service => ['container', `${projectName}-${service}`]),
    ...['e2e_surrealdb_data', 'e2e_api_data_protection'].map(volume => ['volume', `${projectName}_${volume}`]),
    ...['default', 'e2e_webhooks'].map(network => ['network', `${projectName}_${network}`]),
  ]
  for (const [kind, command, format] of [
    ['container', ['ps', '-a'], '{{.Names}}'], ['volume', ['volume', 'ls'], '{{.Name}}'], ['network', ['network', 'ls'], '{{.Name}}'],
  ]) {
    for (const label of ['com.docker.compose.project', 'io.podman.compose.project']) {
      const result = spawnSync(engine, [...command, '--filter', `label=${label}=${projectName}`, '--format', format], {
        encoding: 'utf8', windowsHide: true, timeout: 15_000,
      })
      if (result.error || result.status !== 0) fail(`Cannot inventory ${kind} ownership; no resource was changed.`)
      for (const name of result.stdout.trim().split(/\r?\n/).filter(Boolean))
        if (!resources.some(([type, existing]) => type === kind && existing === name)) resources.push([kind, name])
    }
  }
  for (const [kind, name] of resources) {
    const result = spawnSync(engine, [kind, 'inspect', name], { encoding: 'utf8', windowsHide: true, timeout: 15_000 })
    if (result.error) fail(`Cannot inspect ${kind} ${name}.`)
    if (result.status !== 0) {
      if (/no (?:such )?(?:container|volume|network)|not found|does not exist/i.test(result.stderr ?? '')) continue
      fail(`Cannot establish ownership of ${kind} ${name}; no resource was changed.`)
    }
    const value = JSON.parse(result.stdout)[0]
    const labels = value.Config?.Labels ?? value.Labels ?? value.labels ?? {}
    if (!expected || labels['com.azoa.e2e.owner'] !== expected)
      fail(`Refusing unknown ${kind} ${name}; no resource was changed.`)
  }
  ownerId = expected ?? randomBytes(16).toString('hex')
  if (!expected) writeFileSync(ownershipFile, ownerId + '\n')
}

function resetGeneratedDirectory(target) {
  const relative = path.relative(generated, target)
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) fail('Refusing a generated-directory reset outside this run.')
  rmSync(target, { recursive: true, force: true })
  mkdirSync(target, { recursive: true })
}

/** Derives the real API build with exactly one test-only restore property. */
function ensureApiDockerfile() {
  const source = readFileSync(path.join(repositoryRoot, 'Dockerfile'))
  const anchor = Buffer.from('RUN dotnet restore AZOA.WebAPI.csproj')
  const offsets = []
  for (let start = source.indexOf(anchor); start >= 0; start = source.indexOf(anchor, start + 1)) {
    const end = start + anchor.length
    if ((start === 0 || source[start - 1] === 10) && (end === source.length || source[end] === 10 || source[end] === 13)) offsets.push(end)
  }
  if (offsets.length !== 1) fail('API Dockerfile restore anchor drifted; refusing an unverified local build.')
  const offset = offsets[0]
  const flag = Buffer.from(' -p:NuGetAudit=false')
  const derived = Buffer.concat([source.subarray(0, offset), flag, source.subarray(offset)])
  if (!Buffer.concat([derived.subarray(0, offset), derived.subarray(offset + flag.length)]).equals(source))
    fail('Local API Dockerfile changed bytes beyond the approved restore property.')
  writeFileSync(apiDockerfile, derived)
  writeFileSync(path.join(generated, 'api-Dockerfile-proof.json'), JSON.stringify({
    sourceSha256: createHash('sha256').update(source).digest('hex'),
    generatedSha256: createHash('sha256').update(derived).digest('hex'),
    insertedBytes: flag.length, exactRestoreMatches: offsets.length, inverseByteEquality: true,
  }, null, 2) + '\n')
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

  resetGeneratedDirectory(trustDirectory)
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

/** Verifies the provider's resolved build inputs without emitting its secret-bearing config. */
function verifyResolvedBuilds(environment) {
  const { executable, preArgs } = composeCommand()
  const result = spawnSync(executable, [...preArgs, '-p', projectName, '-f', composeFile, 'config', '--format', 'json'], {
    cwd: repositoryRoot, env: environment, encoding: 'utf8', windowsHide: true, maxBuffer: 8 * 1024 * 1024,
  })
  if (result.error || result.status !== 0)
    fail('Compose must support resolved JSON configuration; no build was started. Use a compatible Compose v2 provider.')
  let resolved
  try { resolved = JSON.parse(result.stdout) } catch { fail('Compose returned invalid resolved JSON; no build was started.') }
  if (!resolved || resolved.name !== projectName || !resolved.services || Object.keys(resolved.services).length !== 4)
    fail('Resolved compose project or service inventory differs from the isolated stack; no build was started.')
  if (!/^[a-f0-9]{40}$/.test(environment.SOURCE_REVISION))
    fail('The E2E source revision must be an exact Git SHA; no build was started.')
  const proof = []
  for (const [name, expectedDockerfile] of [
    ['azoa-api', apiDockerfile], ['azoa-frontend', path.join(e2eRoot, 'stack', 'frontend.Dockerfile')],
  ]) {
    const service = resolved.services[name]
    const build = service?.build
    if (!build || build.dockerfile_inline !== undefined || typeof build.context !== 'string' || typeof build.dockerfile !== 'string'
      || path.relative(repositoryRoot, path.resolve(repositoryRoot, build.context)) !== ''
      || path.relative(expectedDockerfile, path.resolve(repositoryRoot, build.context, build.dockerfile)) !== '')
      fail('A resolved E2E Dockerfile or build context differs from the reviewed path; no build was started.')
    if (name === 'azoa-api' && build.args?.SOURCE_REVISION !== environment.SOURCE_REVISION)
      fail('The resolved API source revision differs from Git HEAD; no build was started.')
    proof.push({ service: name, dockerfile: path.relative(repositoryRoot, expectedDockerfile).split(path.sep).join('/') })
  }
  for (const [name, suffix] of [['surrealdb', 'surrealdb'], ['azoa-api', 'api'], ['azoa-frontend', 'frontend'], ['webhook-receiver', 'webhook-receiver']]) {
    const service = resolved.services[name]
    if (service?.container_name !== `${projectName}-${suffix}` || service.labels?.['com.azoa.e2e.owner'] !== ownerId)
      fail('Resolved E2E container ownership differs from this run; no build was started.')
  }
  writeFileSync(path.join(generated, 'resolved-build-proof.json'), JSON.stringify({
    project: projectName, sourceRevision: environment.SOURCE_REVISION, builds: proof,
  }, null, 2) + '\n')
  console.log('[e2e-stack] resolved project, Dockerfiles, ownership and source revision verified without exposing configuration.')
}

/** Volumes are only ever removed for THIS project (its file declares `name: azoa-e2e`). */
function removeStack(environment, withVolumes) {
  establishOwnership()
  if (withVolumes) {
    const declared = /^name:\s*(\S+)/m.exec(readFileSync(path.join(repositoryRoot, composeFile), 'utf8'))?.[1]
    if (declared !== '${AZOA_E2E_PROJECT:-azoa-e2e}') fail('Refusing to remove volumes for an unexpected compose project declaration.')
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
  resetGeneratedDirectory(probeOutput)
  const user = typeof process.getuid === 'function' ? ['--user', `${process.getuid()}:${process.getgid()}`] : []
  run(containerEngine(), [
    'run', '--rm', ...user,
    '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp', '-e', 'DOTNET_NOLOGO=1', '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '--mount', `type=bind,source=${probeSource},target=/src,readonly`,
    '--mount', `type=bind,source=${probeOutput},target=/out`,
    sdkImage, 'sh', '-c',
    'mkdir -p /tmp/build && cp /src/AzoaEgressProbe.csproj /tmp/build/ && cp /src/EgressProbe.csx /tmp/build/Program.cs '
      + '&& dotnet publish /tmp/build/AzoaEgressProbe.csproj -c Release -o /out --nologo -p:NuGetAudit=false',
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
  const git = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot, encoding: 'utf8' })
  const revision = git.stdout?.trim()
  if (git.error || git.status !== 0 || !/^[a-f0-9]{40}$/.test(revision ?? ''))
    fail('Cannot establish the exact Git HEAD for the E2E build.')
  const settings = { ...readEnvironmentFile(), ...process.env }
  return {
    AZOA_E2E_FRONTEND_PORT: '3200',
    AZOA_E2E_API_PORT: '5200',
    ...settings,
    SOURCE_REVISION: revision,
    AZOA_E2E_PROJECT: projectName,
    AZOA_E2E_GENERATED_SUBDIR: generatedSubdir,
    AZOA_E2E_OWNER_ID: ownerId,
    AZOA_E2E_API_DOCKERFILE: path.relative(repositoryRoot, apiDockerfile).split(path.sep).join('/'),
    AZOA_E2E_IDENTOMAT_UPSTREAM_PORT: settings.AZOA_E2E_IDENTOMAT_UPSTREAM_PORT ?? settings.AZOA_E2E_IDENTOMAT_PORT ?? '9443',
    AZOA_E2E_STRIPE_UPSTREAM_PORT: settings.AZOA_E2E_STRIPE_UPSTREAM_PORT ?? settings.AZOA_E2E_STRIPE_PORT ?? '9444',
  }
}

mkdirSync(generated, { recursive: true })
establishOwnership()

if (command === 'prepare' || command === 'up') {
  ensureApiDockerfile()
  ensureCertificates()
  const secretsChanged = ensureSecrets()
  const environment = stackEnvironment()
  verifyResolvedBuilds(environment)
  if (secretsChanged) {
    // The operator credential is seeded once per volume: a kept volume would keep the OLD password
    // and every operator login would answer 401. New secrets therefore always start a fresh volume.
    console.log(`[e2e-stack] new secrets written; removing any existing ${projectName} stack and its volumes so the operator is re-seeded.`)
    removeStack(environment, true)
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
