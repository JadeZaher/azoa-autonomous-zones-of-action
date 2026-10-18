import { auditPinPath } from "./paths.js";
import { readJsonFile, writeJsonFileSecure } from "./fsutil.js";

export interface AuditPin {
  seq: number;
  hash: string;
}

export function loadAuditPin(profile: string): AuditPin | null {
  return readJsonFile<AuditPin>(auditPinPath(profile));
}

export function saveAuditPin(profile: string, pin: AuditPin): void {
  writeJsonFileSecure(auditPinPath(profile), pin);
}
