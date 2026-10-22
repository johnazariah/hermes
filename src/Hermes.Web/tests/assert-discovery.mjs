import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const cli = fileURLToPath(new URL('../node_modules/@playwright/test/cli.js', import.meta.url));
const result = spawnSync(process.execPath, [cli, 'test', '--list', '--reporter=json'], { encoding: 'utf8' });
if (result.status !== 0) throw new Error(`Playwright discovery failed: ${result.stderr || result.stdout}`);
const report = JSON.parse(result.stdout);
const counts = new Map();
function visit(suites) {
  for (const suite of suites) {
    for (const spec of suite.specs ?? []) {
      const file = spec.file.replaceAll('\\', '/').split('/').pop();
      counts.set(file, (counts.get(file) ?? 0) + spec.tests.length);
    }
    visit(suite.suites ?? []);
  }
}
visit(report.suites ?? []);
const expected = new Map([
  ['smoke.spec.ts', 19], ['chat.spec.ts', 7], ['reclassification.spec.ts', 9],
  ['pipeline.spec.ts', 2], ['command-palette.spec.ts', 6],
  ['sync.spec.ts', 2],
  ['access.spec.ts', 13],
  ['preview.spec.ts', 3],
]);
if (JSON.stringify([...counts].sort()) !== JSON.stringify([...expected].sort())) {
  throw new Error(`Browser inventory mismatch. Expected ${JSON.stringify([...expected])}; discovered ${JSON.stringify([...counts])}`);
}
console.log(`Discovered ${[...counts.values()].reduce((sum, value) => sum + value, 0)} browser cases across ${counts.size} suites; excluded Blazor promotion tests are not claimed as executed.`);
