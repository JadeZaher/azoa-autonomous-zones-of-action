/** Mirrors the exact otpauth URI format mandated by plan.md §8 CLI binding, server-side. */
export function buildOtpauthUri(host: string, username: string, secretBase32: string): string {
  const label = `AZOA%20Node%20(${host}):${username}`;
  const issuer = encodeURIComponent(`AZOA Node (${host})`);
  return `otpauth://totp/${label}?secret=${secretBase32}&issuer=${issuer}&algorithm=SHA1&digits=6&period=30`;
}
