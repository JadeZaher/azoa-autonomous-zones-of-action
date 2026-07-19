import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  AzoaApiClient,
  type TenantCustodialAccountStatus,
  type TenantCustodialCapabilities,
  type TenantKycSession,
  type TenantKycSubmission,
} from "../../src/api/client.js";

const fetchMock = vi.fn();
vi.stubGlobal("fetch", fetchMock);

function ok(result: unknown) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, message: "Success", result }),
  });
}

describe("AzoaApiClient tenant custody contract", () => {
  let client: AzoaApiClient;

  beforeEach(() => {
    fetchMock.mockReset();
    client = new AzoaApiClient({ baseUrl: "https://azoa.example", apiKey: "tenant-key" });
  });

  it("ensures the external subject with a stable Idempotency-Key", async () => {
    fetchMock.mockReturnValueOnce(ok({
      tenantId: "tenant",
      externalSubject: "user:42",
      ardanovaUserId: "user:42",
      kycStatus: "Unknown",
      walletReady: true,
      ready: false,
    }));

    await client.ensureTenantCustodialAccount("user:42", "ardanova-custodial-account:stable-key");

    expect(fetchMock.mock.calls[0][0]).toBe(
      "https://azoa.example/api/tenant/custodial-accounts/user%3A42"
    );
    const init = fetchMock.mock.calls[0][1] as { method: string; headers: Record<string, string> };
    expect(init.method).toBe("PUT");
    expect(init.headers["Idempotency-Key"]).toBe("ardanova-custodial-account:stable-key");
  });

  it("submits only document references to the tenant KYC route", async () => {
    fetchMock.mockReturnValueOnce(ok({
      submissionId: "submission",
      status: "Pending",
      submittedAt: "2026-07-18T00:00:00Z",
    }));

    await client.submitTenantKyc("user-42", [{
      type: "GOVERNMENT_ID",
      referenceUrl: "https://documents.example/object/42",
      fileName: "identity.pdf",
      mimeType: "application/pdf",
      fileSizeBytes: 1024,
    }]);

    const [url, init] = fetchMock.mock.calls[0] as [string, { method: string; body: string }];
    expect(url).toBe(
      "https://azoa.example/api/tenant/custodial-accounts/user-42/kyc/submissions"
    );
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body)).toEqual({
      documents: [{
        type: "GOVERNMENT_ID",
        referenceUrl: "https://documents.example/object/42",
        fileName: "identity.pdf",
        mimeType: "application/pdf",
        fileSizeBytes: 1024,
      }],
    });
  });

  it("begins KYC with a stable Idempotency-Key", async () => {
    fetchMock.mockReturnValueOnce(ok({
      provider: "manual",
      hostedVerification: false,
      acceptsDocumentReferences: true,
      developmentSimulation: true,
      instructions: "Upload document references.",
    }));

    const result = await client.beginTenantKyc("user-42", "ardanova-kyc-session:stable-key");

    const [url, init] = fetchMock.mock.calls[0] as [string, { method: string; headers: Record<string, string> }];
    expect(url).toBe(
      "https://azoa.example/api/tenant/custodial-accounts/user-42/kyc/session"
    );
    expect(init.method).toBe("POST");
    expect(init.headers["Idempotency-Key"]).toBe("ardanova-kyc-session:stable-key");
    expect(result.ok && result.value.developmentSimulation).toBe(true);
  });

  it("surfaces whether tenant custody is a development simulation", async () => {
    fetchMock.mockReturnValueOnce(ok({
      enabled: true,
      walletChain: "Algorand",
      custodyMode: "DevelopmentOnly",
      custodyAvailable: true,
      blockchainProviderAvailable: true,
      kycProvider: "manual",
      kycAvailable: true,
      hostedVerification: false,
      acceptsDocumentReferences: true,
      developmentSimulation: true,
      identityReady: true,
      kycReady: true,
      walletProvisioningReady: true,
      ready: true,
    }));

    const result = await client.getTenantCustodialCapabilities();

    expect(result.ok && result.value.developmentSimulation).toBe(true);
  });

  it("preserves explicit nulls in nullable tenant response fields", async () => {
    const account: TenantCustodialAccountStatus = {
      tenantId: "tenant",
      externalSubject: "user-42",
      ardanovaUserId: "user-42",
      avatarId: null,
      walletId: null,
      walletAddress: null,
      kycStatus: "Unknown",
      identityReady: false,
      kycReady: false,
      walletReady: false,
      ready: false,
      unavailableReason: null,
    };
    const capabilities: TenantCustodialCapabilities = {
      enabled: true,
      walletChain: "Algorand",
      custodyMode: "Disabled",
      custodyAvailable: false,
      blockchainProviderAvailable: false,
      kycProvider: "",
      kycAvailable: false,
      hostedVerification: false,
      acceptsDocumentReferences: false,
      developmentSimulation: false,
      identityReady: true,
      kycReady: false,
      walletProvisioningReady: false,
      ready: false,
      unavailableReason: null,
    };
    const session: TenantKycSession = {
      provider: "manual",
      hostedVerification: false,
      acceptsDocumentReferences: true,
      developmentSimulation: true,
      verificationUrl: null,
      expiresAt: null,
      instructions: null,
    };
    const submission: TenantKycSubmission = {
      submissionId: "submission",
      status: "Pending",
      submittedAt: "2026-07-18T00:00:00Z",
      expiresAt: null,
    };
    fetchMock
      .mockReturnValueOnce(ok(account))
      .mockReturnValueOnce(ok(capabilities))
      .mockReturnValueOnce(ok(session))
      .mockReturnValueOnce(ok(submission));

    const accountResult = await client.getTenantCustodialAccount("user-42");
    const capabilityResult = await client.getTenantCustodialCapabilities();
    const sessionResult = await client.beginTenantKyc("user-42", "stable-key");
    const submissionResult = await client.submitTenantKyc("user-42", []);

    expect(accountResult.ok && accountResult.value.walletId).toBeNull();
    expect(capabilityResult.ok && capabilityResult.value.unavailableReason).toBeNull();
    expect(sessionResult.ok && sessionResult.value.verificationUrl).toBeNull();
    expect(sessionResult.ok && sessionResult.value.expiresAt).toBeNull();
    expect(sessionResult.ok && sessionResult.value.instructions).toBeNull();
    expect(submissionResult.ok && submissionResult.value.expiresAt).toBeNull();
  });
});
