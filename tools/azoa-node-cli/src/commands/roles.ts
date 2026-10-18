import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { createRealInputSource, type InputSource } from "../io/input.js";
import { emit } from "../io/output.js";
import { mutateWithStepUpRetry } from "../mutate.js";
import type { CommonOptions } from "./admins.js";

interface RoleDto {
  id: string;
  name: string;
  builtIn: boolean;
  permissions: string[];
  revision: number;
}

export async function runRolesList(options: CommonOptions): Promise<RoleDto[]> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<RoleDto[]>("GET", "/api/node-admin/roles");
  emit(options.json, data.map((r) => `${r.id}\t${r.name}${r.builtIn ? " (built-in)" : ""}\t[${r.permissions.join(", ")}]`).join("\n") || "(no roles)", data);
  return data;
}

export async function runRolesCreate(
  args: { name: string; permissions: string[] },
  options: CommonOptions,
  input: InputSource = createRealInputSource(),
): Promise<RoleDto> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const data = await mutateWithStepUpRetry<RoleDto>(resolved, "POST", "/api/node-admin/roles", args, input);
  emit(options.json, `Created role '${data.name}' (${data.id}).`, data);
  return data;
}

/**
 * `PUT /roles/{id}` takes the SAME `{ name, permissions }` body as create (confirmed against
 * `NodeAdminRoleRequest`/`UpdateRoleAsync`, lane A1 f4d5a77e) — it is a full replace, including
 * the name, and can rename the role. `--name` is therefore required, not optional.
 */
export async function runRolesUpdate(
  id: string,
  args: { name: string; permissions: string[] },
  options: CommonOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(resolved, "PUT", `/api/node-admin/roles/${encodeURIComponent(id)}`, args, input);
  emit(options.json, `Updated role ${id} (name='${args.name}').`, { id, ...args });
}

export async function runRolesDelete(
  id: string,
  options: CommonOptions,
  input: InputSource = createRealInputSource(),
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await mutateWithStepUpRetry(resolved, "DELETE", `/api/node-admin/roles/${encodeURIComponent(id)}`, undefined, input);
  emit(options.json, `Deleted role ${id}.`, { id });
}
