import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AzoaApiClient, normalizeApiKeyScopes } from '../../src/api/client.js';
import { SdkErrorCode, userFacingMessage } from '../../src/core/errors.js';

// Live QA 2026-10-18 wire shapes (round4-post-qa-deferrals.md H1-H4).
const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);
const reply = (status: number, body: unknown) => ({ ok: status >= 200 && status < 300, status, json: async () => body });

beforeEach(() => fetchMock.mockReset());

describe('credential exchanges never run the session refresh (H1)', () => {
  it('a wrong password surfaces the server refusal, not a network failure, and sends no bearer', async () => {
    const onTokenRefresh = vi.fn(async () => { throw new Error('No session token available.'); });
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', onTokenRefresh });
    fetchMock.mockResolvedValueOnce(reply(401, { isError: true, message: 'Invalid credentials.' }));

    const result = await api.login('qa@example.com', 'wrong');

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.error.code).toBe(SdkErrorCode.API_ERROR);
    expect(result.error.status).toBe(401);
    expect(userFacingMessage(result.error)).toBe('Invalid credentials.');
    expect(onTokenRefresh).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(new Headers(fetchMock.mock.calls[0][1].headers).get('Authorization')).toBeNull();
  });

  it('a stale session token is not attached to a fresh login', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'stale' });
    fetchMock.mockResolvedValueOnce(reply(200, { isError: false, result: 'new.jwt.token' }));
    const result = await api.login('qa@example.com', 'right');
    expect(result.ok && result.value).toBe('new.jwt.token');
    expect(new Headers(fetchMock.mock.calls[0][1].headers).get('Authorization')).toBeNull();
  });

  it('an ordinary 401 still refreshes once and retries', async () => {
    const onTokenRefresh = vi.fn(async () => 'fresh');
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'expired', onTokenRefresh });
    fetchMock.mockResolvedValueOnce(reply(401, {})).mockResolvedValueOnce(reply(200, { isError: false, result: [] }));
    const result = await api.listApiKeys();
    expect(result.ok).toBe(true);
    expect(onTokenRefresh).toHaveBeenCalledTimes(1);
    expect(new Headers(fetchMock.mock.calls[1][1].headers).get('Authorization')).toBe('Bearer fresh');
  });
});

describe('API key scopes arrive as CSV and are normalized to a list (H2)', () => {
  it('lists keys with the real wire shape, including a revoked key and a full-access key', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(200, { isError: false, message: 'OK', result: [
      { id: 'a', name: 'ci', keyPrefix: 'azoa_83dd1631b5f', createdDate: '2026-10-18T05:00:00Z', isActive: false, revokedAt: '2026-10-18T05:10:00Z', scopes: 'wallet:manage, nft:mint' },
      { id: 'b', name: 'legacy', keyPrefix: 'azoa_0000000000a', createdDate: '2026-10-18T05:00:00Z', isActive: true, scopes: null },
    ] }));
    const result = await api.listApiKeys();
    expect(result.ok && result.value.map((key) => key.scopes)).toEqual([['wallet:manage', 'nft:mint'], []]);
  });

  it('sends a scope list as the CSV the controller binds', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(200, { isError: false, result: { id: 'a', name: 'ci', key: 'azoa_x', keyPrefix: 'azoa_x', createdDate: '2026-10-18T05:00:00Z', scopes: 'wallet:manage,nft:mint' } }));
    const result = await api.createApiKey({ name: 'ci', scopes: ['wallet:manage', 'nft:mint'] });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body).scopes).toBe('wallet:manage,nft:mint');
    expect(result.ok && result.value.scopes).toEqual(['wallet:manage', 'nft:mint']);
  });

  it.each([
    ['csv', 'a, b,,c ', ['a', 'b', 'c']],
    ['array', ['a', ' b ', ''], ['a', 'b']],
    ['null', null, []],
    ['number', 3, []],
  ] as const)('normalizes %s', (_name, value, expected) => {
    expect(normalizeApiKeyScopes(value)).toEqual(expected);
  });
});

describe('fungible creation list reads the bare array the controller returns (H3)', () => {
  it('returns the bare list', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(200, [{ operation: { operationId: 'op', state: 'AwaitingConsent' } }]));
    const result = await api.listFungibleCreations();
    expect(result.ok && result.value).toHaveLength(1);
    expect(fetchMock.mock.calls[0][0]).toBe('https://azoa.example/api/fungible-creation?offset=0&limit=25');
  });

  it('returns an empty bare list as empty', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(200, []));
    const result = await api.listFungibleCreations();
    expect(result.ok && result.value).toEqual([]);
  });

  it('refuses a body that is not a list instead of reading it as empty', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(200, { isError: false, result: null }));
    const result = await api.listFungibleCreations();
    expect(result.ok).toBe(false);
  });
});

describe('validation problem details reach the caller (H4)', () => {
  it('flattens ValidationProblemDetails.errors into the error message', async () => {
    const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 't' });
    fetchMock.mockResolvedValueOnce(reply(400, {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { Username: ['Username may contain only letters, numbers, and underscores.'] },
    }));
    const result = await api.request('PUT', '/api/user/11111111-1111-1111-1111-111111111111', { username: 'wallet-solana-x' });
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.error.status).toBe(400);
    expect(userFacingMessage(result.error)).toBe('Username may contain only letters, numbers, and underscores.');
  });
});
