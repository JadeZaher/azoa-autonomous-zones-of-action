import { beforeEach, expect, it, vi } from 'vitest';
import { AzoaApiClient } from '../../src/api/client.js';
const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);
beforeEach(() => { fetchMock.mockReset(); fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ result: { operation: { operationId: 'saved', state: 'AwaitingConsent' } } }) }); });

it('preserves exact uint64 supply and original request key through response loss', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'actor' });
  const request = { chainType: 'Algorand', name: 'Unit', unitName: 'UNT', total: '18446744073709551615', decimals: 2 };
  fetchMock.mockRejectedValueOnce(new Error('response lost'));
  await api.fungibleMint(request, { idempotencyKey: 'one-original-key' });
  await api.fungibleMint(request, { idempotencyKey: 'one-original-key' });
  expect(fetchMock.mock.calls[1][1].body).toBe(fetchMock.mock.calls[0][1].body);
  expect(JSON.parse(fetchMock.mock.calls[1][1].body).total).toBe(request.total);
  expect(new Headers(fetchMock.mock.calls[1][1].headers).get('Idempotency-Key')).toBe('one-original-key');
  expect(fetchMock.mock.calls[1][0]).toBe('https://azoa.example/api/nft/fungible-mint');
});

it('rejects imprecise or noncanonical supply before network admission', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example' });
  for (const total of [9007199254740992, '01', '18446744073709551616', '1e3'])
    await expect(api.fungibleMint({ chainType: 'Algorand', name: 'Unit', unitName: 'UNT', total, decimals: 2 }, { idempotencyKey: 'same' })).rejects.toThrow();
  expect(fetchMock).not.toHaveBeenCalled();
});

it('keeps participant and operator continuation routes separate and retains exact command bodies', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'authenticated' });
  const command = { requestId: '11111111-1111-1111-1111-111111111111', version: 7 };
  await api.continueFungibleCreation('original/id', command);
  await api.continuePlatformGenesisCreation('original/id', command);
  expect(fetchMock.mock.calls[0][0]).toContain('/api/fungible-creation/original%2Fid/continue');
  expect(fetchMock.mock.calls[1][0]).toContain('/api/admin/platform-token/creation/original%2Fid/continue');
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  await api.attachFungibleCreation('original', { ...command, projectionAuthorityId: 'verified-lineage' });
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ ...command, projectionAuthorityId: 'verified-lineage' });
});
