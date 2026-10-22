import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, type Page } from '@playwright/test';

const web = fileURLToPath(new URL('..', import.meta.url));
export async function ownedHarness() {
  const metadata: unknown = JSON.parse(await readFile(resolve(web, 'test-results/issue28-harness.json'), 'utf8'));
  if (typeof metadata !== 'object' || metadata === null) throw new Error('Missing owned browser harness');
  const data = metadata as { version: number; configDir: string; root: string; port: number; dll: string };
  const fixtures = resolve(web, 'test-results/issue28-fixtures') + sep;
  if (data.version !== 1 || !resolve(data.root).startsWith(fixtures)
    || resolve(data.configDir) !== resolve(data.root, 'config')
    || !Number.isInteger(data.port) || data.port < 1024 || [21740, 21741, 21742].includes(data.port)
    || await readFile(resolve(data.root, '.synthetic-smoke'), 'utf8') !== 'Owned synthetic browser fixture\n') {
    throw new Error('Browser harness ownership check failed');
  }
  if (new URL(process.env.HERMES_WEB_BASE_URL!).origin !== `http://127.0.0.1:${data.port}`) {
    throw new Error('Browser harness target differs from the selected owned Service');
  }
  return data;
}

export async function approveDisplayedPairing(page: Page, code: string): Promise<void> {
  if (!/^[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){2}$/.test(code)) throw new Error('Invalid displayed pairing code');
  const metadata = await ownedHarness();
  const env = { ...process.env };
  delete env.HERMES_MCP_TOKEN;
  const result = await new Promise<{ code: number | null; stdout: string }>((fulfil, reject) => {
    const child = spawn('dotnet', [metadata.dll, 'pair-ui', code, '--config-dir', metadata.configDir, '--port', String(metadata.port)], {
      cwd: metadata.root, env, stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stdout = '';
    const timeout = setTimeout(() => { child.kill(); reject(new Error('Owner CLI confirmation exceeded its deadline')); }, 10_000);
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.stdout.on('data', chunk => {
      if (stdout.length > 16_384) { child.kill(); reject(new Error('Owner CLI output exceeded its bound')); return; }
      stdout += chunk.toString('utf8');
    });
    child.stderr.resume();
    child.on('close', code => { clearTimeout(timeout); fulfil({ code, stdout }); });
    child.stdin.on('error', () => { /* A failing CLI can close stdin before confirmation. Exit is asserted below. */ });
    child.stdin.end('yes\n');
  });
  expect(result.code, 'The real early owner CLI must approve over its private OS-owner pipe; no HTTP mint fallback').toBe(0);
  expect(result.stdout).toContain(new URL(page.url()).origin);
  expect(result.stdout).toContain(`http://127.0.0.1:${metadata.port}`);
  for (const capability of ['config.read', 'model.invoke', 'mutate']) expect(result.stdout).toContain(capability);
}

export async function unlock(page: Page): Promise<void> {
  const panel = page.getByRole('region', { name: 'Hermes access' });
  if ((await panel.innerText()).includes('Access: authenticated')) return;
  await expect(panel).toContainText(/Access: (anonymous|expired|unavailable)/);
  await panel.getByRole('button', { name: /Unlock Hermes|Reconnect/ }).click();
  const code = page.getByTestId('pairing-code');
  await expect(code).toHaveText(/^[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){2}$/);
  await approveDisplayedPairing(page, (await code.innerText()).trim());
  await expect(panel).toContainText('Access: authenticated');
}
