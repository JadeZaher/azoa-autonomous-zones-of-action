import { beforeEach, expect, it, vi } from 'vitest';
import { AzoaApiClient } from '../../src/api/client.js';

const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);
beforeEach(() => fetchMock.mockReset());

const operation = 'a'.repeat(64);
const mandate = 'b'.repeat(64);

it('preserves exact full-width cost consent after a lost response and reconciles without signing', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'token' });
  const request = { requestId: 'original-request', version: 7, quoteId: 'original-quote', maximumNetworkFeeBaseUnits: '18446744073709551615' };
  fetchMock.mockRejectedValueOnce(new Error('response lost'));
  await api.continueHoldCommand(operation, request);
  fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ isError: false, result: { state: 'Unresolved' } }) });
  await api.continueHoldCommand(operation, request);
  await api.reconcileHoldCommand(operation, { kind: 'Transfer', consent: request });
  expect(fetchMock.mock.calls[0][1].body).toBe(fetchMock.mock.calls[1][1].body);
  expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ kind: 'Transfer', consent: request });
  expect(fetchMock.mock.calls[2][0]).toBe(`https://azoa.example/api/hold-commands/${operation}/reconcile-request`);
});

it('uses separate opening disclosure consent, recipient consent and mandate management routes', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'token' });
  fetchMock.mockResolvedValue({ ok: true, status: 202, json: async () => ({ isError: false, result: {} }) });
  const recovery = { requestId: 'saved', version: 3, reason: 'Withdraw stale disclosure' };
  await api.acceptHoldOpening(operation, { requestId: 'opening', version: 2, draftHash: 'exact' });
  await api.acceptHoldRecipient(operation, { requestId: 'recipient', version: 3, quoteId: 'q', maximumNetworkFeeBaseUnits: '1000' });
  await api.withdrawHoldOpening(operation, recovery);
  await api.reconcileHoldOpeningRecovery(operation, { kind: 'WorkflowOpeningWithdraw', request: recovery });
  await api.revokeHoldReleaseMandate(mandate, recovery);
  await api.listHoldCommands(25, 25);
  expect(fetchMock.mock.calls.map(call => call[0])).toEqual([
    `https://azoa.example/api/hold-commands/${operation}/accept-opening`,
    `https://azoa.example/api/hold-commands/${operation}/accept`,
    `https://azoa.example/api/hold-commands/${operation}/withdraw-opening`,
    `https://azoa.example/api/hold-commands/${operation}/reconcile-opening-recovery`,
    `https://azoa.example/api/hold-release-mandates/${mandate}/revoke`,
    'https://azoa.example/api/hold-commands?offset=25&limit=25',
  ]);
});

it('refuses a hold or mandate id that is not a server-issued reference before any request', async () => {
  const api = new AzoaApiClient({ baseUrl: 'https://azoa.example', token: 'token' });
  const recovery = { requestId: 'saved', version: 3, reason: 'Cancel' };

  await expect(api.cancelHoldCommand('../hold-release-mandates/x', recovery)).rejects.toThrow(/64-character lowercase hex/);
  await expect(api.revokeHoldReleaseMandate(mandate.toUpperCase(), recovery)).rejects.toThrow(/64-character lowercase hex/);
  expect(fetchMock).not.toHaveBeenCalled();
});
