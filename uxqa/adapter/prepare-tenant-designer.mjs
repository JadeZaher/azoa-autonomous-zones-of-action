import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

// Coordinator-only marked fixture; see ../fixtures/tenant-designer.md.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const handle = 'qa-uxqa-ai-tenant';
const marker = 'uxqa-p02-designer-tenant';
const expectedScopes = ['tenant:provision', 'wallet:manage', 'kyc:read', 'kyc:submit'].sort().join(',');
let stage = 'SCENARIO_AUTHORIZATION';
const requireCondition = (ok) => { if (!ok) throw new Error('fixture refused'); };

function guard() {
  stage = 'ENVIRONMENT_GUARD';
  execFileSync(process.execPath, [path.join(root, 'uxqa/adapter/adapter.mjs'), 'guard'], {
    cwd: root, env: process.env, stdio: ['ignore', 'pipe', 'pipe'], timeout: 60_000,
  });
}

async function request(api, method, route, token, data) {
  const response = await fetch(api + route, {
    method, redirect: 'error', signal: AbortSignal.timeout(20_000),
    headers: { accept: 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...(data ? { 'content-type': 'application/json' } : {}) },
    body: data ? JSON.stringify(data) : undefined,
  });
  requireCondition(response.ok);
  const envelope = await response.json();
  requireCondition(!envelope.isError && envelope.result !== undefined);
  return envelope.result;
}

try {
  requireCondition(process.env.UXQA_COORDINATOR_FIXTURE === 'tenant-designer-p02');
  guard();
  const apiPort = process.env.AZOA_E2E_API_PORT || '5200';
  const url = new URL(process.env.AZOA_E2E_API_URL || `http://localhost:${apiPort}`);
  requireCondition(url.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(url.hostname) && url.port === apiPort
    && url.pathname === '/' && !url.username && !url.password && !url.search && !url.hash);
  const api = url.origin;
  stage = 'FIXTURE_ACCOUNT';
  const accounts = JSON.parse(fs.readFileSync(path.join(root, 'uxqa/.secrets/accounts.json'), 'utf8'));
  const account = accounts[handle];
  requireCondition(account && account.status === 'registered' && account.email.startsWith(handle + '-') && account.email.endsWith('@example.com')
    && /^uxqa-[a-f0-9]{12}$/.test(account.username));
  const login = () => request(api, 'POST', '/api/user/login', null, { email: account.email, password: account.password });
  let token = await login();
  requireCondition(typeof token === 'string');
  const claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString('utf8'));
  const id = claims.sub;
  requireCondition(typeof id === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(id));
  const profile = await request(api, 'GET', `/api/user/${id}`, token);
  requireCondition(profile.id?.toLowerCase() === id.toLowerCase() && profile.email === account.email && profile.username === account.username
    && profile.isActive === true && !profile.ownerTenantId && ['dapp:user', 'dapp:developer'].includes(profile.dappRole));
  const preflight = await fetch(api + '/api/action-designer', {
    redirect: 'error', signal: AbortSignal.timeout(10_000), headers: { authorization: `Bearer ${token}` },
  });
  requireCondition([200, 403].includes(preflight.status));

  let operator;
  const operatorToken = async () => {
    if (operator) return operator;
    stage = 'DISPOSABLE_OPERATOR_LOGIN';
    const e2eRoot = path.resolve(process.env.UXQA_E2E_ROOT || path.join(root, 'frontend/e2e'));
    requireCondition(e2eRoot.startsWith(root + path.sep));
    const generated = fs.readFileSync(path.join(e2eRoot, '.generated/e2e.env'), 'utf8');
    const line = generated.split(/\r?\n/).find((value) => value.startsWith('AZOA_E2E_OPERATOR_PASSWORD='));
    const password = process.env.AZOA_E2E_OPERATOR_PASSWORD || line?.slice(line.indexOf('=') + 1);
    requireCondition(password && password.length >= 20);
    const session = await request(api, 'POST', '/api/operator/session', null, { username: 'node-operator', password });
    requireCondition(typeof session.accessToken === 'string');
    operator = session.accessToken;
    return operator;
  };
  if (profile.dappRole === 'dapp:user') {
    const authority = await operatorToken();
    guard();
    stage = 'MARKED_DEVELOPER_ROLE';
    const changed = await request(api, 'PUT', `/api/user/${id}/dapp-role`, authority, { role: 'dapp:developer' });
    requireCondition(changed.id?.toLowerCase() === id.toLowerCase() && changed.dappRole === 'dapp:developer');
    token = await login();
  }

  stage = 'OWN_TENANT_CREDENTIALS';
  const keys = await request(api, 'GET', '/api/apikey', token);
  requireCondition(Array.isArray(keys));
  const existing = keys.filter((key) => key.name === marker && key.isActive && !key.revokedAt && new Date(key.expiresAt).getTime() > Date.now());
  requireCondition(existing.length <= 1);
  let key = existing[0];
  if (key) requireCondition(key.scopes.split(',').map((scope) => scope.trim()).sort().join(',') === expectedScopes
    && new Date(key.expiresAt).getTime() <= Date.now() + 24 * 60 * 60 * 1000 + 5 * 60 * 1000);
  else {
    const authority = await operatorToken();
    guard();
    stage = 'MARKED_TENANT_CREDENTIAL';
    const issued = await request(api, 'POST', '/api/apikey/tenant', authority, { tenantUserId: id, name: marker, expiresInDays: 1 });
    key = { id: issued.id, scopes: issued.scopes, expiresAt: issued.expiresAt };
    requireCondition(key.scopes?.split(',').map((scope) => scope.trim()).sort().join(',') === expectedScopes);
  }
  requireCondition(typeof key.id === 'string' && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(key.id));
  stage = 'VERIFIED_TENANT_CONTEXT';
  token = await login();
  const availability = await request(api, 'GET', '/api/action-designer', token);
  requireCondition(availability.tenantId?.toLowerCase() === id.toLowerCase());
  fs.writeFileSync(path.join(root, 'uxqa/.secrets/ai-tenant-fixture.json'), JSON.stringify({
    handle, userId: id, keyId: key.id, keyName: marker, expiresAt: key.expiresAt,
    assistantAvailable: availability.available === true, preparedAt: new Date().toISOString(),
  }), { mode: 0o600 });
  console.log(`PREPARED_TENANT count=1 developer=verified authority=verified assistant_available=${availability.available === true}`);
} catch {
  console.log(`REFUSED ${stage}`);
  process.exitCode = 2;
}
