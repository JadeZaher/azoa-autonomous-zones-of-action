import { Command } from "commander";
import { runAdminsActivate, runAdminsCreate, runAdminsList, runAdminsReactivate, runAdminsRevoke, runAdminsRevokeSessions, runAdminsSetRoles, runAdminsShow, runAdminsSuspend } from "./commands/admins.js";
import { runApi } from "./commands/api.js";
import { runAuditTail, runAuditVerify } from "./commands/audit.js";
import { runEnroll } from "./commands/enroll.js";
import { runKeygen } from "./commands/keygen.js";
import { runKeysAdd, runKeysRevoke } from "./commands/keysCmd.js";
import { runLogin } from "./commands/login.js";
import { runLogout } from "./commands/logout.js";
import { runMfaReset } from "./commands/mfa.js";
import { runProfileAdd, runProfileList, runProfileUse } from "./commands/profile.js";
import { runRolesCreate, runRolesDelete, runRolesList, runRolesUpdate } from "./commands/roles.js";
import { runRootEnv } from "./commands/rootEnv.js";
import { runRootRevokeSessions } from "./commands/rootRevokeSessions.js";
import { runSessionRevoke, runSessionsList } from "./commands/sessions.js";
import { runWhoami } from "./commands/whoami.js";
import { AzoaCliError, EXIT_CODES } from "./errors.js";
import { setGlobalNodeOverride } from "./globalNodeOverride.js";

const program = new Command();
program
  .name("azoa-node")
  .description("Azoa node admin identity, RBAC, MFA and break-glass root CLI")
  .option("--profile <name>", "profile to use (defaults to the current profile)")
  .option("--json", "machine-readable JSON output", false)
  .option("--node <origin>", "override the profile's configured node origin for this invocation")
  .hook("preAction", (thisCommand) => {
    const opts = thisCommand.opts<{ node?: string }>();
    setGlobalNodeOverride(opts.node);
  });

function jsonFlag(cmd: Command): boolean {
  return Boolean(cmd.optsWithGlobals<{ json?: boolean }>().json);
}

function profileFlag(cmd: Command): string | undefined {
  return cmd.optsWithGlobals<{ profile?: string }>().profile;
}

/**
 * Every command action below is registered through this wrapper. Commander
 * always calls the action with `(...positionalArgs, localOptions, command)`
 * — the last element is always the Command instance, which is what lets us
 * resolve global --profile/--json/--node (declared on `program`, not on the
 * leaf subcommand) via `optsWithGlobals()`.
 */
function action<Args extends unknown[]>(fn: (...args: Args) => Promise<void> | void) {
  return async (...args: Args) => {
    const cmd = args[args.length - 1] as Command;
    try {
      await fn(...args);
    } catch (err) {
      const json = jsonFlag(cmd);
      if (err instanceof AzoaCliError) {
        if (json) {
          console.error(JSON.stringify({ isError: true, message: err.message, code: err.code }, null, 2));
        } else {
          console.error(`Error: ${err.message}`);
        }
        process.exitCode = err.exitCode;
        return;
      }
      console.error(`Error: ${(err as Error).message}`);
      process.exitCode = EXIT_CODES.NETWORK;
    }
  };
}

program
  .command("keygen")
  .description("generate an Ed25519 keypair (root keys require a passphrase)")
  .requiredOption("--name <name>", "local key file name")
  .option("--kind <kind>", "root|admin", "admin")
  .option("--no-passphrase", "skip passphrase encryption (admin keys only)")
  .action(action(async (opts: { name: string; kind: "root" | "admin"; passphrase: boolean }, cmd: Command) => {
    await runKeygen({ name: opts.name, kind: opts.kind, passphrase: opts.passphrase !== false, json: jsonFlag(cmd) });
  }));

const rootCmd = program.command("root").description("break-glass root key management");
rootCmd
  .command("env")
  .description("render NodeAdmin__RootPublicKeys / NodeAdmin__RootKeyRevision")
  .option("--profile-keys <names>", "comma-separated root profile names whose public keys to include")
  .option("--add-public-key <keys>", "comma-separated raw b64url public keys to add")
  .option("--remove <publicKeyOrFingerprint>", "key to remove (bumps NodeAdmin__RootKeyRevision)")
  .option("--current <keys>", "current NodeAdmin__RootPublicKeys value (when not using --railway)")
  .option("--current-revision <n>", "current NodeAdmin__RootKeyRevision value (when not using --railway)", Number)
  .option("--railway", "fetch/set via the Railway CLI", false)
  .option("--project <id>")
  .option("--environment <id>")
  .option("--service <id>")
  .option("--yes", "required to actually apply --railway changes", false)
  .action(action(async (opts: Record<string, unknown>, cmd: Command) => {
    await runRootEnv({
      profiles: typeof opts.profileKeys === "string" ? opts.profileKeys.split(",").map((s) => s.trim()).filter(Boolean) : [],
      addPublicKey: typeof opts.addPublicKey === "string" ? opts.addPublicKey.split(",").map((s) => s.trim()).filter(Boolean) : [],
      remove: opts.remove as string | undefined,
      current: opts.current as string | undefined,
      currentRevision: opts.currentRevision as number | undefined,
      railway: Boolean(opts.railway),
      project: opts.project as string | undefined,
      environment: opts.environment as string | undefined,
      service: opts.service as string | undefined,
      yes: Boolean(opts.yes),
      json: jsonFlag(cmd),
    });
  }));
rootCmd
  .command("revoke-sessions")
  .description("end every root session on this node (POST /root/revoke-sessions)")
  .option("--yes", "skip the confirmation prompt", false)
  .action(action(async (opts: { yes: boolean }, cmd: Command) => {
    await runRootRevokeSessions({ profile: profileFlag(cmd), yes: opts.yes, json: jsonFlag(cmd) });
  }));

const profileCmd = program.command("profile").description("local CLI profiles");
profileCmd
  .command("add <name>")
  .requiredOption("--origin <origin>", "node public origin, e.g. https://node.example.test (collides with the global --node flag, hence --origin here)")
  .requiredOption("--kind <kind>", "root|admin")
  .requiredOption("--key-name <name>", "key file name under ~/.azoa/keys/")
  .option("--admin-id <id>")
  .option("--username <username>")
  .option("--use", "make this the current profile", false)
  .action(action(async (name: string, opts: Record<string, unknown>, cmd: Command) => {
    runProfileAdd({
      name,
      node: opts.origin as string,
      kind: opts.kind as "root" | "admin",
      keyName: opts.keyName as string,
      adminId: opts.adminId as string | undefined,
      username: opts.username as string | undefined,
      use: Boolean(opts.use),
      json: jsonFlag(cmd),
    });
  }));
profileCmd.command("list").action(action(async (_opts: Record<string, unknown>, cmd: Command) => runProfileList({ json: jsonFlag(cmd) })));
profileCmd
  .command("use <name>")
  .action(action(async (name: string, _opts: Record<string, unknown>, cmd: Command) => runProfileUse(name, { json: jsonFlag(cmd) })));

program
  .command("login")
  .option("--root", "log in as root (profile must be kind=root)", false)
  .option("--totp <code>", "skip the interactive TOTP prompt (scripting)")
  .action(action(async (opts: { root: boolean; totp?: string }, cmd: Command) => {
    await runLogin({ profile: profileFlag(cmd), root: opts.root, totp: opts.totp, json: jsonFlag(cmd) });
  }));

program
  .command("logout")
  .action(action(async (_opts: Record<string, unknown>, cmd: Command) => runLogout({ profile: profileFlag(cmd), json: jsonFlag(cmd) })));
program
  .command("whoami")
  .action(action(async (_opts: Record<string, unknown>, cmd: Command) => {
    await runWhoami({ profile: profileFlag(cmd), json: jsonFlag(cmd) });
  }));

const sessionsCmd = program.command("sessions").description("list/revoke node-admin sessions");
sessionsCmd
  .command("list")
  .option("--admin <id>", "list another admin's sessions (requires admins.read)")
  .action(action(async (opts: { admin?: string }, cmd: Command) => {
    await runSessionsList({ profile: profileFlag(cmd), admin: opts.admin, json: jsonFlag(cmd) });
  }));
sessionsCmd
  .command("revoke <handle>")
  .action(action(async (handle: string, _opts: Record<string, unknown>, cmd: Command) => runSessionRevoke(handle, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));

const adminsCmd = program.command("admins").description("node admin CRUD, keys, MFA, enrollment");
adminsCmd
  .command("list")
  .action(action(async (_opts: Record<string, unknown>, cmd: Command) => { await runAdminsList({ profile: profileFlag(cmd), json: jsonFlag(cmd) }); }));
adminsCmd
  .command("show <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => { await runAdminsShow(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) }); }));
adminsCmd
  .command("create")
  .requiredOption("--username <username>")
  .requiredOption("--display-name <name>")
  .requiredOption("--role <roleIds>", "comma-separated role ids")
  .action(action(async (opts: { username: string; displayName: string; role: string }, cmd: Command) => {
    await runAdminsCreate(
      { username: opts.username, displayName: opts.displayName, roleIds: opts.role.split(",").map((s) => s.trim()).filter(Boolean) },
      { profile: profileFlag(cmd), json: jsonFlag(cmd) },
    );
  }));
adminsCmd
  .command("suspend <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => runAdminsSuspend(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));
adminsCmd
  .command("reactivate <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => runAdminsReactivate(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));
adminsCmd
  .command("revoke <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => runAdminsRevoke(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));
adminsCmd
  .command("revoke-sessions <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => runAdminsRevokeSessions(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));
adminsCmd
  .command("activate <id>")
  .requiredOption("--fingerprint <fp>", "the enrolled key's fingerprint, confirmed out of band")
  .action(action(async (id: string, opts: { fingerprint: string }, cmd: Command) => runAdminsActivate(id, opts.fingerprint, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));
adminsCmd
  .command("roles <id>")
  .requiredOption("--set <roleIds>", "comma-separated role ids to set")
  .action(action(async (id: string, opts: { set: string }, cmd: Command) => runAdminsSetRoles(id, opts.set.split(",").map((s) => s.trim()).filter(Boolean), { profile: profileFlag(cmd), json: jsonFlag(cmd) })));

const keysCmd = program.command("keys").description("admin credential (key) management");
keysCmd
  .command("add")
  .option("--admin-id <id>", "defaults to the logged-in admin (self)")
  .requiredOption("--key-name <name>", "local name for the new key file")
  .option("--label <label>", "label shown in `admins show`", "cli")
  .option("--no-passphrase", "skip passphrase encryption for this key")
  .action(action(async (opts: { adminId?: string; keyName: string; label: string; passphrase: boolean }, cmd: Command) => {
    await runKeysAdd({
      profile: profileFlag(cmd),
      targetAdminId: opts.adminId,
      keyName: opts.keyName,
      label: opts.label,
      passphrase: opts.passphrase !== false,
      json: jsonFlag(cmd),
    });
  }));
keysCmd
  .command("revoke <adminId> <fingerprint>")
  .action(action(async (adminId: string, fingerprint: string, _opts: Record<string, unknown>, cmd: Command) => runKeysRevoke(adminId, fingerprint, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));

const mfaCmd = program.command("mfa").description("MFA reset for another admin");
mfaCmd
  .command("reset <adminId>")
  .action(action(async (adminId: string, _opts: Record<string, unknown>, cmd: Command) => runMfaReset(adminId, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));

program
  .command("enroll")
  .description("complete an admin enrollment (reads the token from stdin or a hidden prompt, never argv)")
  .requiredOption("--origin <origin>", "the node's canonical public origin (collides with the global --node flag, hence --origin here; must match exactly at login time)")
  .option("--profile-name <name>", "local profile name to create (distinct from the global --profile, which selects an existing one)", "default")
  .option("--key-name <name>", "local key file name (defaults to the profile name)")
  .option("--label <label>", "label shown in `admins show`", "enroll")
  .option("--no-passphrase", "skip passphrase encryption for this key")
  .option("--force", "overwrite an existing key file of the same name", false)
  .action(action(async (opts: { origin: string; profileName: string; keyName?: string; label: string; passphrase: boolean; force: boolean }, cmd: Command) => {
    await runEnroll({
      node: opts.origin,
      profile: opts.profileName,
      keyName: opts.keyName ?? opts.profileName,
      label: opts.label,
      passphrase: opts.passphrase !== false,
      force: opts.force,
      json: jsonFlag(cmd),
    });
  }));

const rolesCmd = program.command("roles").description("RBAC role CRUD");
rolesCmd
  .command("list")
  .action(action(async (_opts: Record<string, unknown>, cmd: Command) => { await runRolesList({ profile: profileFlag(cmd), json: jsonFlag(cmd) }); }));
rolesCmd
  .command("create")
  .requiredOption("--name <name>")
  .requiredOption("--permissions <permissions>", "comma-separated permission names")
  .action(action(async (opts: { name: string; permissions: string }, cmd: Command) => {
    await runRolesCreate({ name: opts.name, permissions: opts.permissions.split(",").map((s) => s.trim()).filter(Boolean) }, { profile: profileFlag(cmd), json: jsonFlag(cmd) });
  }));
rolesCmd
  .command("update <id>")
  .requiredOption("--name <name>", "PUT /roles/{id} is a full replace, including the name — not optional")
  .requiredOption("--permissions <permissions>", "comma-separated permission names")
  .action(action(async (id: string, opts: { name: string; permissions: string }, cmd: Command) => {
    await runRolesUpdate(
      id,
      { name: opts.name, permissions: opts.permissions.split(",").map((s) => s.trim()).filter(Boolean) },
      { profile: profileFlag(cmd), json: jsonFlag(cmd) },
    );
  }));
rolesCmd
  .command("delete <id>")
  .action(action(async (id: string, _opts: Record<string, unknown>, cmd: Command) => runRolesDelete(id, { profile: profileFlag(cmd), json: jsonFlag(cmd) })));

const auditCmd = program.command("audit").description("node_admin_audit tail + hash-chain verification");
auditCmd
  .command("tail")
  .option("--after <seq>", "only rows after this seq", Number)
  .option("--limit <n>", "max rows (server caps at 500)", Number)
  .option("--follow", "not implemented — cut per plan.md §8", false)
  .action(action(async (opts: { after?: number; limit?: number; follow: boolean }, cmd: Command) => {
    await runAuditTail({ profile: profileFlag(cmd), after: opts.after, limit: opts.limit, follow: opts.follow, json: jsonFlag(cmd) });
  }));
auditCmd
  .command("verify")
  .option("--full", "ignore the local pin and re-verify the whole chain from genesis (server may rate-limit this)", false)
  .option("--from-seq <seq>", "explicit server-side verify window start (defaults to the local pin)", Number)
  .action(action(async (opts: { full: boolean; fromSeq?: number }, cmd: Command) =>
    runAuditVerify({ profile: profileFlag(cmd), full: opts.full, fromSeq: opts.fromSeq, json: jsonFlag(cmd) }),
  ));

program
  .command("api <method> <path>")
  .description("authenticated passthrough to any /api/... route")
  .option("--data <json>", "JSON body, or @file to read from a file")
  .action(action(async (method: string, path: string, opts: { data?: string }, cmd: Command) => {
    await runApi(method.toUpperCase(), path, { profile: profileFlag(cmd), data: opts.data, json: jsonFlag(cmd) });
  }));

program.exitOverride((err) => {
  if (err.code === "commander.helpDisplayed" || err.code === "commander.version") {
    process.exit(0);
  }
  process.exitCode = EXIT_CODES.USAGE;
});

await program.parseAsync(process.argv);
