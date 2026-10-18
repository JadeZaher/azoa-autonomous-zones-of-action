import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";
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
): Promise<RoleDto> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const { data } = await httpClientFor(resolved).call<RoleDto>("POST", "/api/node-admin/roles", args);
  emit(options.json, `Created role '${data.name}' (${data.id}).`, data);
  return data;
}

export async function runRolesUpdate(
  id: string,
  args: { permissions: string[] },
  options: CommonOptions,
): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call("PUT", `/api/node-admin/roles/${encodeURIComponent(id)}`, args);
  emit(options.json, `Updated role ${id}.`, { id, ...args });
}

export async function runRolesDelete(id: string, options: CommonOptions): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  await httpClientFor(resolved).call("DELETE", `/api/node-admin/roles/${encodeURIComponent(id)}`);
  emit(options.json, `Deleted role ${id}.`, { id });
}
