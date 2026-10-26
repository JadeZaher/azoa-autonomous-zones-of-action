// Dev-only Identomat vendor sidecar: a standalone port of tests/.../Http/Infrastructure/IdentomatVendor.cs.
// See tools/dev-kyc-vendor/AGENTS.md for the protocol map, modes, and why this exists.
import http from 'node:http';
import { createHash, randomBytes } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export const Verdicts = Object.freeze(['Pending', 'ManualCheck', 'Approved', 'Rejected']);
export const Modes = Object.freeze(['approve-on-begin', 'approve-on-poll', 'manual']);
const MaxBodyBytes = 64 * 1024;

/** L4: a fresh random secret for a bare launch that supplies no env override (dev-node-up.ps1 always does). */
const randomSecret = (byteLength) => randomBytes(byteLength).toString('hex');

/** M2(a)/(c): true for the loopback hostnames the sidecar is allowed to bind to or be addressed as. */
export function isLoopbackHostname(value) {
  const raw = String(value ?? '').trim();
  const bracketed = /^\[([^\]]+)\](?::\d+)?$/.exec(raw);
  // A bare (unbracketed) IPv6 literal has 2+ colons; only a bracketed one may carry a port
  // suffix, so only the host:port shorthand (hostname/IPv4, a single colon) gets its port
  // stripped here — otherwise "::1" itself would be mistaken for a "::" host with a ":1" port.
  const bare = bracketed ? bracketed[1]
    : raw.includes('::') || raw.split(':').length > 2 ? raw
    : raw.replace(/:\d+$/, '');
  const hostname = bare.toLowerCase();
  return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '::1' || hostname.startsWith('127.');
}

/** Reads sidecar configuration from the environment (all values have dev defaults). */
export function configFromEnv(env = process.env) {
  return {
    host: env.DEV_KYC_HOST || '127.0.0.1',
    apiPort: Number(env.DEV_KYC_API_PORT || 8031),
    widgetPort: Number(env.DEV_KYC_WIDGET_PORT || 8032),
    companyKey: env.DEV_KYC_COMPANY_KEY || randomSecret(20),
    kycConfigId: env.DEV_KYC_KYC_CONFIG_ID || 'dev-kyc-config',
    kybConfigId: env.DEV_KYC_KYB_CONFIG_ID || 'dev-kyb-config',
    callbackApiKey: env.DEV_KYC_CALLBACK_API_KEY || randomSecret(24),
    nodeBaseUrl: env.DEV_KYC_NODE_BASE_URL || 'http://127.0.0.1:5000',
    callbackPath: env.DEV_KYC_CALLBACK_PATH || '/api/kyc/callback/identomat',
    callbackDelaysMs: (env.DEV_KYC_CALLBACK_DELAYS_MS || '1500,5000,15000')
      .split(',').map((value) => Number(value.trim())).filter((value) => Number.isFinite(value) && value >= 0),
    mode: Modes.includes(env.DEV_KYC_MODE) ? env.DEV_KYC_MODE : 'approve-on-begin',
  };
}

/** Creates the vendor (state + both HTTP servers). Call start() to listen, stop() to close. */
export function createVendor(config, log = (entry) => process.stdout.write(JSON.stringify(entry) + '\n')) {
  const sessions = [];
  const timers = new Set();
  const state = { mode: config.mode };

  const json = (res, status, body) => {
    const text = typeof body === 'string' ? body : JSON.stringify(body);
    res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
    res.end(text);
    return status;
  };
  const empty = (res, status) => { res.writeHead(status); res.end(); return status; };

  const findSession = (key) => [...sessions].reverse().find((session) =>
    session.id === key || session.sessionToken === key || session.clientUserId === key);

  const scheduleCallbacks = (session) => {
    const claimed = session.verdict === 'Approved' ? 'APPROVED' : session.verdict === 'Rejected' ? 'REJECTED' : 'MANUAL_CHECK';
    for (const delay of config.callbackDelaysMs) {
      const timer = setTimeout(async () => {
        timers.delete(timer);
        const body = JSON.stringify({
          callbackApiKey: config.callbackApiKey, type: 'session-change', sessionId: session.id, result: claimed,
        });
        try {
          const response = await fetch(new URL(config.callbackPath, config.nodeBaseUrl), {
            method: 'POST', headers: { 'content-type': 'application/json' }, body,
          });
          log({ ts: new Date().toISOString(), event: 'callback', sessionId: session.id, claimed, status: response.status });
        } catch (error) {
          log({ ts: new Date().toISOString(), event: 'callback', sessionId: session.id, claimed, error: String(error.cause?.code || error.message) });
        }
      }, delay);
      timer.unref?.();
      timers.add(timer);
    }
  };

  const setVerdict = (session, verdict) => {
    session.verdict = verdict;
    if (verdict !== 'Pending') scheduleCallbacks(session);
  };

  // ── API host (camelCase): /begin, KYB /result, AML screening ────────────────
  const apiRoutes = {
    '/begin': (body, res) => {
      const configId = body.configId;
      if (configId !== config.kycConfigId && configId !== config.kybConfigId)
        return json(res, 200, { argumentError: 'unknown configId' });
      const id = 'idm-session-' + randomBytes(16).toString('hex');
      const session = {
        id, sessionToken: id, clientUserId: String(body.clientUserId ?? ''),
        isBusiness: configId === config.kybConfigId, verdict: 'Pending', deleted: false,
        createdAt: new Date().toISOString(),
      };
      sessions.push(session);
      if (state.mode === 'approve-on-begin') setVerdict(session, 'Approved');
      const widgetUrl = `http://${config.host}:${config.widgetPort}/?session_token=${session.sessionToken}`;
      return json(res, 200, { id: session.id, url: widgetUrl });
    },
    '/result': (body, res) => {
      const session = sessions.find((candidate) => candidate.isBusiness && candidate.id === body.sessionId);
      if (!session) return json(res, 200, { argumentError: 'session not found' });
      approveOnPoll(session);
      const review = session.verdict === 'Approved' ? 'approved' : session.verdict === 'Rejected' ? 'rejected' : 'in-review';
      return json(res, 200, { result: { stepsResults: [
        { type: 'company', status: 'approved',
          result: { companyName: 'Dev Sidecar Holdings', registrationNumber: 'DEV-0001', registrationCountry: 'US' } },
        { type: 'beneficiaries', status: 'approved', result: [
          { entityType: 'individual', positions: ['director'], _id: 'participant-' + session.id, sessionIds: ['beneficiary-' + session.id] },
        ] },
        { type: 'kyb-review', status: review },
      ] } });
    },
    '/search-screening-person': (_body, res) => json(res, 200, { result: [] }),
    '/monitor-screening-query': (_body, res) => json(res, 200, { result: 'monitor-' + randomBytes(16).toString('hex') }),
    '/remove-screening-query-from-monitoring': (_body, res) => json(res, 200, { result: true }),
    '/list-screening-monitoring-records': (_body, res) =>
      json(res, 200, { result: { start: 0, limit: 100, total: 0, data: [] } }),
  };

  const approveOnPoll = (session) => {
    if (state.mode === 'approve-on-poll' && session.verdict === 'Pending') session.verdict = 'Approved';
  };

  // ── Widget host (legacy snake_case): authoritative KYC result + delete ─────
  const identityResult = (session) => {
    switch (session.verdict) {
      case 'Approved': {
        // Verdicts are the provider's documented UPPERCASE outcomes (IdentomatKycProviderService
        // treats anything else as non-terminal, which parks the submission as Pending).
        // The person is derived from the applicant: the node's person-uniqueness gate binds one
        // person to one user, so a shared fake human would refuse every applicant after the first.
        const who = createHash('sha256').update(session.clientUserId).digest('hex').slice(0, 8);
        const day = (parseInt(who.slice(0, 2), 16) % 28) + 1;
        return { result: 'APPROVED', person: {
          first_name: 'Dev', last_name: 'Sidecar-' + who, birthday: `1990-04-${String(day).padStart(2, '0')}`,
          citizenship: 'US', document_number: 'D' + who, issuing_state: 'US' } };
      }
      case 'Rejected':
        return { result: 'REJECTED', reject_reason: { value: 'DOCUMENT_EXPIRED', description: 'Document expired' } };
      case 'ManualCheck':
        return { result: 'MANUAL_CHECK' };
      default:
        return { result: null };
    }
  };

  const widgetPost = (path, body, res) => {
    if (body.company_key !== config.companyKey) return empty(res, 401);
    const token = body.session_token ?? null;
    const session = sessions.find((candidate) => !candidate.isBusiness && candidate.sessionToken === token && !candidate.deleted);
    if (path === '/external-api/delete/') {
      if (!session) return json(res, 200, { argumentError: 'session-not-found' });
      session.deleted = true;
      return json(res, 200, 'true');
    }
    if (!session) return json(res, 200, { result: null });
    approveOnPoll(session);
    return json(res, 200, identityResult(session));
  };

  const widgetPage = (res, token) => {
    const session = sessions.find((candidate) => candidate.sessionToken === token);
    const safe = String(token ?? '').replace(/[^A-Za-z0-9-]/g, '');
    const buttons = Verdicts.map((verdict) =>
      `<button onclick="fetch('/__dev/sessions/${safe}/verdict',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({verdict:'${verdict}'})}).then(()=>location.reload())">${verdict}</button>`).join(' ');
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
    res.end(`<!doctype html><title>Dev Identomat sidecar</title><h1>Dev identity verification</h1>`
      + `<p>DEV ONLY - no real identity check happens here.</p>`
      + `<p>Session <code>${safe}</code>: <strong id="verdict">${session ? session.verdict : 'unknown'}</strong></p>`
      + `<p>${session ? buttons : 'No such session.'}</p>`);
    return 200;
  };

  // ── Control surface (both hosts, loopback only) ────────────────────────────
  const control = (method, path, body, res) => {
    if (method === 'GET' && path === '/__dev/health')
      return json(res, 200, { ok: true, mode: state.mode, sessions: sessions.length });
    if (method === 'GET' && path === '/__dev/sessions')
      return json(res, 200, { mode: state.mode, sessions });
    if (method === 'POST' && path === '/__dev/mode') {
      if (!Modes.includes(body.mode)) return json(res, 400, { error: `mode must be one of ${Modes.join(', ')}` });
      state.mode = body.mode;
      return json(res, 200, { mode: state.mode });
    }
    // Recover a session the node still references after a sidecar restart (in-memory store).
    if (method === 'POST' && path === '/__dev/sessions/inject') {
      if (!body.id) return json(res, 400, { error: 'id is required' });
      if (body.verdict && !Verdicts.includes(body.verdict)) return json(res, 400, { error: `verdict must be one of ${Verdicts.join(', ')}` });
      let session = sessions.find((candidate) => candidate.id === body.id);
      if (!session) {
        session = { id: body.id, sessionToken: body.sessionToken ?? body.id, additionalSessionId: null,
          clientUserId: body.clientUserId ?? 'injected', isBusiness: Boolean(body.isBusiness),
          verdict: 'Pending', deleted: false };
        sessions.push(session);
      }
      setVerdict(session, body.verdict ?? 'Approved');
      return json(res, 200, session);
    }
    const match = /^\/__dev\/sessions\/([^/]+)\/verdict$/.exec(path);
    if (method === 'POST' && match) {
      if (!Verdicts.includes(body.verdict)) return json(res, 400, { error: `verdict must be one of ${Verdicts.join(', ')}` });
      const session = findSession(decodeURIComponent(match[1]));
      if (!session) return json(res, 404, { error: 'session not found (match by id, session token, or clientUserId)' });
      setVerdict(session, body.verdict);
      return json(res, 200, session);
    }
    return null;
  };

  const readBody = (req) => new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MaxBodyBytes) { reject(new Error('body too large')); req.destroy(); return; }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });

  const handler = (hostName) => async (req, res) => {
    const url = new URL(req.url, 'http://sidecar.local');
    const path = url.pathname;
    let status = 500;
    try {
      // M2(c): DNS-rebinding guard. The bind host is loopback-only (M2(a)), but a browser page
      // can still point fetch() at a hostname whose DNS answer flips to 127.0.0.1 mid-attack;
      // the Host header then carries the attacker's name, not an IP, so CORS never sees it as
      // cross-origin. Refusing any Host that is not itself loopback closes that gap.
      if (!isLoopbackHostname(req.headers.host)) { status = empty(res, 421); return; }
      // M2(b): every vendor-protocol endpoint the node calls sends JSON (IdentomatHttpTransport
      // uses JsonContent.Create on all of /begin, /result, /external-api/result/, and
      // /external-api/delete/), so this refusal is never scoped down to just /__dev/.
      if (req.method === 'POST') {
        const contentType = String(req.headers['content-type'] || '').toLowerCase();
        if (!contentType.startsWith('application/json')) { status = empty(res, 415); return; }
      }
      const raw = req.method === 'POST' ? await readBody(req) : '';
      let body = {};
      if (raw.length > 0) {
        try { body = JSON.parse(raw) ?? {}; } catch { status = empty(res, 400); return; }
      }
      const controlled = path.startsWith('/__dev/') ? control(req.method, path, body, res) : null;
      if (controlled !== null) { status = controlled; return; }
      if (hostName === 'api') {
        const route = apiRoutes[path];
        if (!route) { status = empty(res, 404); return; }
        if (req.method !== 'POST') { status = empty(res, 405); return; }
        if (body.companyKey !== config.companyKey) { status = empty(res, 401); return; }
        status = route(body, res);
        return;
      }
      if (req.method === 'GET' && path === '/') { status = widgetPage(res, url.searchParams.get('session_token')); return; }
      if (req.method === 'POST' && (path === '/external-api/result/' || path === '/external-api/delete/')) {
        status = widgetPost(path, body, res);
        return;
      }
      status = empty(res, 404);
    } catch (error) {
      if (!res.headersSent) status = json(res, 500, { error: String(error.message) });
    } finally {
      log({ ts: new Date().toISOString(), host: hostName, method: req.method, path, status });
    }
  };

  const apiServer = http.createServer(handler('api'));
  const widgetServer = http.createServer(handler('widget'));

  const listen = (server, port) => new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, config.host, () => resolve(server.address().port));
  });

  return {
    sessions,
    state,
    async start() {
      // M2(a): refuse to bind anywhere but loopback. This is a dev-only identity-verification
      // bypass; binding it to a routable address would let anything on the network complete
      // KYC/KYB through it.
      if (!isLoopbackHostname(config.host))
        throw new Error(`Refusing to start: DEV_KYC_HOST must be loopback (127.0.0.1, ::1, or localhost), got "${config.host}".`);
      const apiPort = await listen(apiServer, config.apiPort);
      const widgetPort = await listen(widgetServer, config.widgetPort);
      config.apiPort = apiPort;
      config.widgetPort = widgetPort;
      return { apiBaseUrl: `http://${config.host}:${apiPort}`, widgetBaseUrl: `http://${config.host}:${widgetPort}` };
    },
    async stop() {
      for (const timer of timers) clearTimeout(timer);
      timers.clear();
      await Promise.all([apiServer, widgetServer].map((server) => new Promise((resolve) => server.close(() => resolve()))));
    },
  };
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const config = configFromEnv();
  const vendor = createVendor(config);
  const urls = await vendor.start();
  process.stdout.write(JSON.stringify({ ts: new Date().toISOString(), event: 'listening', mode: config.mode, ...urls }) + '\n');
  const shutdown = async () => { await vendor.stop(); process.exit(0); };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}
