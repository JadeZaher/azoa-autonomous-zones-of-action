import fs from 'node:fs';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { pathToFileURL, fileURLToPath } from 'node:url';

// Local disposable account/session adapter; see ../AGENTS.md.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const lib = process.env.UXQA_PLUGIN_LIB;
const runtime = process.env.UXQA_CONTAINER_RUNTIME || 'podman';
const secretDir = path.join(root, 'uxqa/.secrets');
const accountsFile = path.join(secretDir, 'accounts.json');
const e2eRoot = path.resolve(process.env.UXQA_E2E_ROOT || path.join(root, 'frontend/e2e'));
let stage = 'CONFIGURATION';
const requireCondition = (ok, reason) => { if (!ok) throw new Error(reason); };

function inspect(args) {
  return execFileSync(runtime, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 15_000 }).trim();
}

function origin(value, port) {
  const url = new URL(value);
  requireCondition(url.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(url.hostname)
    && url.port === String(port) && url.pathname === '/' && !url.username && !url.password && !url.search && !url.hash,
  'only the dedicated loopback E2E origins are allowed');
  return url.origin;
}

function inTargetGuard(frontendPort, apiPort) {
  for (const [name, internalPort, hostPort] of [['azoa-e2e-api', '5000/tcp', apiPort], ['azoa-e2e-frontend', '3000/tcp', frontendPort]]) {
    const ports = JSON.parse(inspect(['inspect', '--format', '{{json .NetworkSettings.Ports}}', name]));
    requireCondition(ports[internalPort]?.some((binding) => binding.HostPort === String(hostPort)), 'E2E container port binding mismatch');
  }
  const values = inspect(['exec', 'azoa-e2e-api', 'printenv', 'ASPNETCORE_ENVIRONMENT', 'Kyc__Identomat__BeginBaseUrl', 'Fiat__ApiBaseUrl']).split(/\r?\n/);
  requireCondition(values[0] === 'Development', 'in-target environment is not Development');
  const identomatPort = Number(process.env.AZOA_E2E_IDENTOMAT_UPSTREAM_PORT || 9443);
  const stripePort = Number(process.env.AZOA_E2E_STRIPE_UPSTREAM_PORT || 9444);
  requireCondition([identomatPort, stripePort].every((port) => Number.isInteger(port) && port >= 1024 && port <= 65535), 'invalid fake-vendor upstream ports');
  for (const [index, port] of [[1, identomatPort], [2, stripePort]]) {
    const vendor = new URL(values[index]);
    requireCondition(vendor.protocol === 'https:' && ['host.docker.internal', 'host.containers.internal'].includes(vendor.hostname)
      && vendor.port === String(port) && !vendor.username && !vendor.password, 'in-target vendors are not E2E fakes');
  }
}

async function configuration() {
  requireCondition(lib, 'set UXQA_PLUGIN_LIB to the installed plugin lib directory');
  requireCondition(['podman', 'docker'].includes(runtime), 'unsupported container runtime');
  const { loadStudy, importPlaywright } = await import(pathToFileURL(path.join(lib, 'study-config.mjs')).href);
  const { assertTestTarget } = await import(pathToFileURL(path.join(lib, 'guard.mjs')).href);
  const study = loadStudy(root);
  const frontendPort = process.env.AZOA_E2E_FRONTEND_PORT || '3200';
  const apiPort = process.env.AZOA_E2E_API_PORT || '5200';
  requireCondition(/^\d{4,5}$/.test(frontendPort) && /^\d{4,5}$/.test(apiPort), 'invalid E2E ports');
  const base = origin(study.config.baseUrl, frontendPort);
  const api = origin(process.env.AZOA_E2E_API_URL || `http://localhost:${apiPort}`, apiPort);
  stage = 'PLUGIN_GUARD';
  await assertTestTarget(study);
  stage = 'IN_TARGET_GUARD';
  inTargetGuard(frontendPort, apiPort);
  for (const [url, code] of [[base + '/api/health', 'FRONTEND_NOT_READY'], [api + '/health', 'API_NOT_READY']]) {
    stage = code;
    const health = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(10_000) });
    requireCondition(health.ok, 'dedicated E2E stack is not healthy');
  }
  return { study, importPlaywright, base, api, frontendPort, apiPort };
}

function privateOutput(output) {
  const destination = path.resolve(root, output);
  requireCondition(destination.startsWith(secretDir + path.sep), 'session output must be inside uxqa/.secrets');
  return destination;
}

function generatedOperatorPassword() {
  requireCondition(e2eRoot.startsWith(root + path.sep), 'E2E fixtures must be inside this workspace');
  const content = fs.readFileSync(path.join(e2eRoot, '.generated/e2e.env'), 'utf8');
  const line = content.split(/\r?\n/).find((value) => value.startsWith('AZOA_E2E_OPERATOR_PASSWORD='));
  requireCondition(line, 'generated E2E operator credential is missing');
  const password = process.env.AZOA_E2E_OPERATOR_PASSWORD || line.slice(line.indexOf('=') + 1);
  requireCondition(password.length >= 20, 'generated E2E operator credential is invalid');
  return password;
}

async function seed(config, input, output) {
  const cast = JSON.parse(fs.readFileSync(path.resolve(root, input), 'utf8'));
  const personas = Array.isArray(cast) ? cast : cast.personas;
  requireCondition(Array.isArray(personas) && personas.length > 0 && personas.length <= 8, 'invalid persona count');
  requireCondition(new Set(personas.map((p) => p.handle)).size === personas.length, 'duplicate persona handles');
  for (const persona of personas) {
    requireCondition(/^qa-uxqa-[a-z0-9-]+$/.test(persona.handle), 'non-study persona handle refused');
    requireCondition(['anonymous', 'member', 'operator'].includes(persona.role), 'unknown persona role');
    if (persona.role === 'operator') requireCondition(persona.handle === 'qa-uxqa-operator', 'operator session restricted to declared fixture persona');
  }
  const destination = privateOutput(output);
  fs.mkdirSync(secretDir, { recursive: true });
  const accounts = fs.existsSync(accountsFile) ? JSON.parse(fs.readFileSync(accountsFile, 'utf8')) : {};
  const { chromium } = await config.importPlaywright(config.study);
  const browser = await chromium.launch({ headless: true, ...(config.study.config.browserChannel ? { channel: config.study.config.browserChannel } : {}) });
  const sessions = [];
  try {
    for (const persona of personas) {
      const context = await browser.newContext();
      try {
        if (persona.role === 'anonymous') {
          sessions.push({ handle: persona.handle, storageState: await context.storageState() });
          continue;
        }
        const page = await context.newPage();
        if (persona.role === 'member') {
          let account = accounts[persona.handle];
          if (!account) {
            const suffix = randomBytes(6).toString('hex');
            account = { username: `uxqa-${suffix}`, email: `${persona.handle}-${suffix}@example.com`, password: `Uxqa-${randomBytes(24).toString('base64url')}-1aA!`, status: 'registration-intent' };
            accounts[persona.handle] = account;
            fs.writeFileSync(accountsFile, JSON.stringify(accounts), { mode: 0o600 });
          }
          if (account.status === 'registration-intent') {
            inTargetGuard(config.frontendPort, config.apiPort);
            const existingLogin = await fetch(config.api + '/api/user/login', {
              method: 'POST', redirect: 'error', signal: AbortSignal.timeout(20_000),
              headers: { 'content-type': 'application/json' },
              body: JSON.stringify({ email: account.email, password: account.password }),
            });
            requireCondition(existingLogin.ok || existingLogin.status === 401, 'study identity recovery unavailable');
            if (!existingLogin.ok) {
              const response = await fetch(config.api + '/api/user/register', {
                method: 'POST', redirect: 'error', signal: AbortSignal.timeout(20_000),
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ username: account.username, email: account.email, password: account.password }),
              });
              requireCondition(response.ok, 'study registration refused');
              const body = await response.json();
              requireCondition(!body.isError, 'study registration rejected');
            }
            account.status = 'registered';
            fs.writeFileSync(accountsFile, JSON.stringify(accounts), { mode: 0o600 });
          }
          await page.goto(config.base + '/login');
          await page.getByLabel('Email', { exact: true }).fill(account.email);
          await page.getByLabel('Password', { exact: true }).fill(account.password);
          await page.getByRole('button', { name: 'Sign in', exact: true }).click();
          await page.waitForURL('**/overview', { timeout: 20_000 });
        } else {
          await page.goto(config.base + '/operator/login');
          await page.getByLabel('Operator username', { exact: true }).fill('node-operator');
          await page.getByLabel('Password', { exact: true }).fill(generatedOperatorPassword());
          await page.getByRole('button', { name: 'Enter operator console', exact: true }).click();
          await page.waitForURL(config.base + '/operator', { timeout: 20_000 });
        }
        sessions.push({ handle: persona.handle, storageState: await context.storageState() });
      } finally {
        await context.close();
      }
    }
    fs.writeFileSync(destination, JSON.stringify(sessions), { mode: 0o600 });
    console.log(`SEEDED ${sessions.length}`);
  } finally {
    await browser.close();
  }
}

try {
  const command = process.argv[2];
  requireCondition(['guard', 'seed', 'link'].includes(command), 'usage: adapter.mjs guard|seed <personas> <out>|link <handle>');
  if (command === 'link') throw new Error('one-time links are unsupported; coordinator may inspect private local storage state');
  const config = await configuration();
  if (command === 'guard') console.log('OK azoa-e2e Development fake-vendors loopback-bindings');
  else {
    stage = 'SEED_PRECONDITION_OR_AUTHENTICATION';
    await seed(config, process.argv[3], process.argv[4]);
  }
} catch {
  console.log(`REFUSED ${stage}`);
  process.exitCode = 2;
}
