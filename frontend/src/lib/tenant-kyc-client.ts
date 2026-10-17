"use client";

import { KYB_REQUIRED } from "azoa-sdk";
import { azoa, isOk } from "@/lib/azoa";
import { KYB_REQUIRED_MESSAGE } from "@/lib/refusal-messages";
import type {
    TenantKycProviderChoiceResponse,
    TenantKycSelectionResponse,
} from "@/lib/operator-contracts";

export class TenantKycRequestError extends Error {
    constructor(
        message: string,
        readonly conflict = false,
        readonly reauthenticate = false,
    ) {
        super(message);
        this.name = "TenantKycRequestError";
    }
}

function humaneTenantError(error: {
    message: string;
    status?: number;
    serverCode?: string;
}): TenantKycRequestError {
    const message = error.message;
    if (error.serverCode === KYB_REQUIRED || message.includes(KYB_REQUIRED)) {
        return new TenantKycRequestError(KYB_REQUIRED_MESSAGE);
    }
    if (
        error.status === 403 &&
        /sign in again.*sensitive account action|RECENT_LOGIN_REQUIRED/i.test(
            message,
        )
    ) {
        return new TenantKycRequestError(
            "Sign in again to confirm this sensitive tenant setting.",
            false,
            true,
        );
    }
    if (/409|conflict|version/i.test(message)) {
        return new TenantKycRequestError(
            "Your tenant configuration changed elsewhere. Refresh before trying again.",
            true,
        );
    }
    if (/403|forbidden|not authorized/i.test(message)) {
        return new TenantKycRequestError(
            "This account is not the administrator of an active Azoa tenant.",
        );
    }
    if (/401|expired/i.test(message)) {
        return new TenantKycRequestError(
            "Your sign-in expired. Sign in again before changing KYC settings.",
        );
    }
    return new TenantKycRequestError(
        "KYC provider settings could not be loaded. Try again.",
    );
}

export async function getTenantKycProviders(): Promise<
    TenantKycProviderChoiceResponse[]
> {
    const result = await azoa.api.request<TenantKycProviderChoiceResponse[]>(
        "GET",
        "/api/tenant/kyc/providers",
    );
    if (isOk(result)) return result.value;
    throw humaneTenantError(result.error);
}

export async function getTenantKycSelection(): Promise<TenantKycSelectionResponse> {
    const result = await azoa.api.request<TenantKycSelectionResponse>(
        "GET",
        "/api/tenant/kyc/provider",
    );
    if (isOk(result)) return result.value;
    throw humaneTenantError(result.error);
}

export async function setTenantKycSelection(
    providerKey: string,
    expectedVersion: number,
): Promise<TenantKycSelectionResponse> {
    const result = await azoa.api.request<TenantKycSelectionResponse>(
        "PUT",
        "/api/tenant/kyc/provider",
        {
            providerKey,
            expectedVersion,
        },
    );
    if (isOk(result)) return result.value;
    throw humaneTenantError(result.error);
}
