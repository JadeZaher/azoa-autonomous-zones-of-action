import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Setup proof only; see ../RUNBOOK.md.
if (!process.env.UXQA_PLUGIN_LIB) throw new Error('UXQA_PLUGIN_LIB is required');
const { uxSession, BASE } = await import(pathToFileURL(path.join(process.env.UXQA_PLUGIN_LIB, 'ux-lib.mjs')).href);
const session = await uxSession({ handle: 'qa-uxqa-smoke', task: 'setup-smoke', pass: '00-setup' });
let outcome = 'BLOCKED';
try {
  await session.page.goto(BASE + '/login');
  await session.page.getByRole('button', { name: 'Sign in', exact: true }).waitFor();
  await session.shot('login');
  await session.axe();
  outcome = 'COMPLETED';
} finally {
  console.log(JSON.stringify(await session.finish(outcome)));
}
