// Protocol tests for the dev sidecar: drive its HTTP surface the way IdentomatKycProviderService/IdentomatKybProvider do.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { createVendor, isLoopbackHostname } from './server.mjs';

const companyKey = 'company-key-for-tests-0000000000000000';

async function withVendor(overrides, run) {
  const callbacks = [];
  const node = http.createServer((req, res) => {
    let raw = '';
    req.on('data', (chunk) => { raw += chunk; });
    req.on('end', () => { callbacks.push({ path: req.url, body: JSON.parse(raw) }); res.writeHead(202); res.end(); });
  });
  await new Promise((resolve) => node.listen(0, '127.0.0.1', resolve));
  const vendor = createVendor({
    host: '127.0.0.1', apiPort: 0, widgetPort: 0, companyKey,
    kycConfigId: 'kyc-cfg', kybConfigId: 'kyb-cfg', callbackApiKey: 'callback-key-for-tests-000000000000000',
    nodeBaseUrl: `http://127.0.0.1:${node.address().port}`, callbackPath: '/api/kyc/callback/identomat',
    callbackDelaysMs: [0], mode: 'approve-on-begin', ...overrides,
  }, () => {});
  const urls = await vendor.start();
  try {
    await run({ ...urls, vendor, callbacks });
  } finally {
    await vendor.stop();
    await new Promise((resolve) => node.close(resolve));
  }
}

const post = async (url, body) => {
  const response = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  const text = await response.text();
  return { status: response.status, body: text.length ? JSON.parse(text) : null };
};
const waitFor = async (predicate) => {
  for (let attempt = 0; attempt < 50 && !predicate(); attempt++) await new Promise((resolve) => setTimeout(resolve, 20));
};

// Raw http.request, not fetch: fetch refuses to let callers override the Host header (it is a
// forbidden header name per the Fetch spec), but the M2(c) DNS-rebinding guard is precisely
// about a request that arrives with a Host header that lies about the connection's origin.
const rawRequest = (urlStr, { method = 'GET', headers = {}, body } = {}) => new Promise((resolve, reject) => {
  const target = new URL(urlStr);
  const request = http.request({
    hostname: target.hostname, port: target.port, path: target.pathname + target.search, method, headers,
  }, (response) => {
    let raw = '';
    response.on('data', (chunk) => { raw += chunk; });
    response.on('end', () => resolve({ status: response.statusCode, body: raw.length ? JSON.parse(raw) : null }));
  });
  request.on('error', reject);
  if (body !== undefined) request.write(body);
  request.end();
});

test('approve-on-begin: KYC session is approved on the widget host and the node gets a keyed callback', async () => {
  await withVendor({}, async ({ apiBaseUrl, widgetBaseUrl, callbacks }) => {
    const begun = await post(`${apiBaseUrl}/begin`, { companyKey, configId: 'kyc-cfg', clientUserId: 'user-1' });
    assert.equal(begun.status, 200);
    assert.ok(begun.body.url.startsWith(`${widgetBaseUrl}/?session_token=`));

    const result = await post(`${widgetBaseUrl}/external-api/result/`, { company_key: companyKey, session_token: begun.body.id });
    assert.equal(result.body.result, 'APPROVED');
    assert.equal(result.body.person.first_name, 'Dev');

    await waitFor(() => callbacks.length > 0);
    assert.deepEqual(callbacks[0].body, {
      callbackApiKey: 'callback-key-for-tests-000000000000000', type: 'session-change', sessionId: begun.body.id, result: 'APPROVED',
    });
  });
});

test('manual mode: the session stays undecided until the control endpoint sets a verdict', async () => {
  await withVendor({ mode: 'manual' }, async ({ apiBaseUrl, widgetBaseUrl }) => {
    const begun = await post(`${apiBaseUrl}/begin`, { companyKey, configId: 'kyc-cfg', clientUserId: 'user-2' });
    const before = await post(`${widgetBaseUrl}/external-api/result/`, { company_key: companyKey, session_token: begun.body.id });
    assert.equal(before.body.result, null);

    for (const [verdict, expected] of [['ManualCheck', 'MANUAL_CHECK'], ['Rejected', 'REJECTED'], ['Approved', 'APPROVED']]) {
      const set = await post(`${apiBaseUrl}/__dev/sessions/user-2/verdict`, { verdict });
      assert.equal(set.status, 200);
      const after = await post(`${widgetBaseUrl}/external-api/result/`, { company_key: companyKey, session_token: begun.body.id });
      assert.equal(after.body.result, expected);
    }
  });
});

test('approve-on-poll: the first authoritative poll approves', async () => {
  await withVendor({ mode: 'approve-on-poll' }, async ({ apiBaseUrl, widgetBaseUrl }) => {
    const begun = await post(`${apiBaseUrl}/begin`, { companyKey, configId: 'kyc-cfg', clientUserId: 'user-3' });
    const polled = await post(`${widgetBaseUrl}/external-api/result/`, { company_key: companyKey, session_token: begun.body.id });
    assert.equal(polled.body.result, 'APPROVED');
  });
});

test('KYB: business sessions answer on the API host /result with the stepsResults shape', async () => {
  await withVendor({}, async ({ apiBaseUrl }) => {
    const begun = await post(`${apiBaseUrl}/begin`, { companyKey, configId: 'kyb-cfg', clientUserId: 'kyb:tenant' });
    const result = await post(`${apiBaseUrl}/result`, { companyKey, sessionId: begun.body.id });
    const review = result.body.result.stepsResults.find((step) => step.type === 'kyb-review');
    assert.equal(review.status, 'approved');
  });
});

test('wrong company key or wrong host is refused like the real vendor', async () => {
  await withVendor({}, async ({ apiBaseUrl, widgetBaseUrl }) => {
    assert.equal((await post(`${apiBaseUrl}/begin`, { companyKey: 'wrong', configId: 'kyc-cfg' })).status, 401);
    assert.equal((await post(`${widgetBaseUrl}/external-api/result/`, { company_key: 'wrong', session_token: 'x' })).status, 401);
    assert.equal((await post(`${widgetBaseUrl}/begin`, { companyKey, configId: 'kyc-cfg' })).status, 404);
    assert.equal((await post(`${apiBaseUrl}/begin`, { companyKey, configId: 'unknown' })).body.argumentError, 'unknown configId');
  });
});

// M2 hardening (A4 fix batch): three refusals the control surface must enforce in code, not
// just by convention of who happens to call it.

test('M2(a): refuses to start unless the bind host is loopback', async () => {
  const vendor = createVendor({
    host: '0.0.0.0', apiPort: 0, widgetPort: 0, companyKey,
    kycConfigId: 'kyc-cfg', kybConfigId: 'kyb-cfg', callbackApiKey: 'callback-key-for-tests-000000000000000',
    nodeBaseUrl: 'http://127.0.0.1:1', callbackPath: '/api/kyc/callback/identomat', callbackDelaysMs: [], mode: 'approve-on-begin',
  }, () => {});
  await assert.rejects(() => vendor.start(), /loopback/i);
});

test('M2(b): refuses a POST whose content-type is not application/json', async () => {
  await withVendor({}, async ({ apiBaseUrl }) => {
    const response = await rawRequest(`${apiBaseUrl}/begin`, {
      method: 'POST',
      headers: { 'content-type': 'text/plain' },
      body: JSON.stringify({ companyKey, configId: 'kyc-cfg', clientUserId: 'user-4' }),
    });
    assert.equal(response.status, 415);
  });
});

test('M2(c): refuses a request whose Host header is not loopback (DNS-rebinding guard)', async () => {
  await withVendor({}, async ({ apiBaseUrl }) => {
    const response = await rawRequest(`${apiBaseUrl}/__dev/health`, {
      method: 'GET',
      headers: { host: 'attacker.example:1' },
    });
    assert.equal(response.status, 421);
  });
});

test('isLoopbackHostname accepts loopback names and addresses, refuses everything else', () => {
  for (const value of ['127.0.0.1', '127.0.0.1:8031', 'localhost', 'LOCALHOST:8031', '::1', '[::1]:8031', '127.5.6.7'])
    assert.equal(isLoopbackHostname(value), true, value);
  for (const value of ['attacker.example', '10.0.0.5', '10.0.0.5:8031', 'widget.identomat.com', undefined, ''])
    assert.equal(isLoopbackHostname(value), false, String(value));
});
