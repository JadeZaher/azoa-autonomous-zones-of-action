import { beforeEach, expect, it, vi } from 'vitest';
import { AzoaApiClient } from '../../src/api/client.js';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);
beforeEach(() => fetchMock.mockReset());

it('preserves exact full-width cost consent after a lost response and reconciles without signing', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'token' });
  const request = { requestId: 'original-request', version: 7, quoteId: 'original-quote', maximumNetworkFeeBaseUnits: '18446744073709551615' };
  fetchMock.mockRejectedValueOnce(new Error('response lost'));
  await api.continueHoldCommand('operation', request);
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: { state: 'Unresolved' } }) });
  await api.continueHoldCommand('operation', request);
  await api.reconcileHoldCommand('operation', { kind: 'Transfer', consent: request });
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ kind: 'Transfer', consent: request });
  expect(fetchMock.mock.calls[2][0]).toBe('https://azoa.example/api/hold-commands/operation/reconcile-request');
});

it('uses separate opening disclosure consent, recipient consent and mandate management routes', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'token' });
  fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ isError: false, result: {} }) });
  const recovery = { requestId: 'saved', version: 3, reason: 'Withdraw stale disclosure' };
  await api.acceptHoldOpening('id/one', { requestId: 'opening', version: 2, draftHash: 'exact' });
  await api.acceptHoldRecipient('id/one', { requestId: 'recipient', version: 3, quoteId: 'q', maximumNetworkFeeBaseUnits: '1000' });
  await api.withdrawHoldOpening('id/one', recovery);
  await api.reconcileHoldOpeningRecovery('id/one', { kind: 'WorkflowOpeningWithdraw', request: recovery });
  await api.revokeHoldReleaseMandate('mandate', recovery);
  await api.listHoldCommands(25, 25);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    'https://azoa.example/api/hold-commands/id%2Fone/accept-opening',
    'https://azoa.example/api/hold-commands/id%2Fone/accept',
    'https://azoa.example/api/hold-commands/id%2Fone/withdraw-opening',
    'https://azoa.example/api/hold-commands/id%2Fone/reconcile-opening-recovery',
    'https://azoa.example/api/hold-release-mandates/mandate/revoke',
    'https://azoa.example/api/hold-commands?offset=25&limit=25',
  ]);
});
