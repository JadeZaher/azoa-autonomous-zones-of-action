/**
 * Error-code catalog (plan.md §8 "Error codes") mapped to exit codes
 * (task spec "Global" section) and a friendly one-line message. `result.code`
 * is the wire discriminator on the AZOAResult envelope.
 */
export const EXIT_CODES = {
  OK: 0,
  USAGE: 2,
  AUTH: 3,
  PERMISSION: 4,
  NETWORK: 5,
  AUDIT_INTEGRITY: 6,
} as const;

const CODE_TO_EXIT: Record<string, number> = {
  AUTH_FAILED: EXIT_CODES.AUTH,
  STEP_UP_REQUIRED: EXIT_CODES.AUTH,
  PERMISSION_DENIED: EXIT_CODES.PERMISSION,
  DOMINANCE_REQUIRED: EXIT_CODES.PERMISSION,
  ESCALATION_REFUSED: EXIT_CODES.PERMISSION,
  LAST_OWNER: EXIT_CODES.PERMISSION,
  SELF_ACTION_REFUSED: EXIT_CODES.PERMISSION,
  ENROLLMENT_ALREADY_STARTED: EXIT_CODES.USAGE,
  ENROLLMENT_INVALID: EXIT_CODES.AUTH,
  FINGERPRINT_MISMATCH: EXIT_CODES.AUTH,
  ADMIN_LOCKED: EXIT_CODES.AUTH,
  USERNAME_TAKEN: EXIT_CODES.USAGE,
  ROLE_IN_USE: EXIT_CODES.USAGE,
  ROLE_BUILT_IN: EXIT_CODES.USAGE,
  UNKNOWN_PERMISSION: EXIT_CODES.USAGE,
  VALIDATION_FAILED: EXIT_CODES.USAGE,
  AUDIT_UNAVAILABLE: EXIT_CODES.NETWORK,
};

const FRIENDLY_MESSAGES: Record<string, string> = {
  AUTH_FAILED: "Authentication failed. Check your key, passphrase, and TOTP code.",
  STEP_UP_REQUIRED: "This action needs a fresh login (re-authenticate within the last 10 minutes).",
  PERMISSION_DENIED: "You do not hold the permission required for this action.",
  DOMINANCE_REQUIRED: "You can only act on admins whose permissions are a subset of your own.",
  ESCALATION_REFUSED: "You cannot grant a permission you do not hold yourself.",
  LAST_OWNER: "Refused: this would leave the node with zero node-owners.",
  SELF_ACTION_REFUSED: "You cannot perform this action on your own admin account.",
  ENROLLMENT_ALREADY_STARTED: "This enrollment has already bound a key. Ask for a fresh enrollment token.",
  ENROLLMENT_INVALID: "The enrollment token/id is invalid, expired, or already used.",
  FINGERPRINT_MISMATCH: "The fingerprint you supplied does not match the enrolled key.",
  ADMIN_LOCKED: "This admin is temporarily locked after repeated failed logins.",
  USERNAME_TAKEN: "That username is already in use.",
  ROLE_IN_USE: "This role is still assigned to one or more admins.",
  ROLE_BUILT_IN: "Built-in roles cannot be modified or deleted.",
  UNKNOWN_PERMISSION: "One or more permissions in the request are not recognized.",
  VALIDATION_FAILED: "The request failed validation.",
  AUDIT_UNAVAILABLE: "The audit log is temporarily unavailable; the action was refused, not silently skipped.",
};

export class AzoaCliError extends Error {
  readonly exitCode: number;
  readonly code?: string;

  constructor(message: string, exitCode: number, code?: string) {
    super(message);
    this.name = "AzoaCliError";
    this.exitCode = exitCode;
    this.code = code;
  }

  static usage(message: string): AzoaCliError {
    return new AzoaCliError(message, EXIT_CODES.USAGE);
  }

  static network(message: string): AzoaCliError {
    return new AzoaCliError(message, EXIT_CODES.NETWORK);
  }

  static auditIntegrity(message: string): AzoaCliError {
    return new AzoaCliError(message, EXIT_CODES.AUDIT_INTEGRITY);
  }

  static fromServerCode(code: string, message: string): AzoaCliError {
    const exitCode = CODE_TO_EXIT[code] ?? EXIT_CODES.NETWORK;
    const friendly = FRIENDLY_MESSAGES[code];
    const combined = friendly ? `${friendly} (${code}: ${message})` : `${code}: ${message}`;
    return new AzoaCliError(combined, exitCode, code);
  }
}
