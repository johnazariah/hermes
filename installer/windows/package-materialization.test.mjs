import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile, lstat, link, symlink, rm } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const project = resolve('installer/windows/Hermes.Installer.wixproj');
const control = resolve('installer/windows/package-materialization.mjs');
const root = resolve('installer/windows/obj', `materialization-controls-${randomUUID()}`);
await mkdir(resolve(root, '..'), { recursive: true });
await mkdir(root);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const synthetic = version => JSON.stringify({
  synthetic: 'copy-target control, not an MSI',
  version, productCode: randomUUID(), packageCode: randomUUID(),
});

async function fixture(name, bytes = synthetic('0.1.0')) {
  const directory = resolve(root, name);
  const intermediate = resolve(directory, 'obj');
  const output = resolve(directory, 'output');
  await mkdir(intermediate, { recursive: true });
  await mkdir(output);
  const source = resolve(intermediate, 'Hermes.Installer.msi');
  const target = resolve(output, 'Hermes.Installer.msi');
  await writeFile(source, bytes);
  return { directory, intermediate, output, source, target, bytes };
}

function copy(f, properties = []) {
  return spawnSync('dotnet', [
    'msbuild', project, '-nologo', '-v:q', '-t:CopyFilesToOutputDirectory',
    `-p:IntermediateOutputPath=${f.intermediate}/`, `-p:OutputPath=${f.output}/`,
    '-p:DebugType=none', ...properties,
  ], { encoding: 'utf8' });
}

function verify(f, expected = sha(f.bytes)) {
  return spawnSync(process.execPath, [control, f.source, f.target, expected], { encoding: 'utf8' });
}

test.after(async () => { await rm(root, { recursive: true, force: true }); });

test('actual pinned WiX copy target preserves independent baseline and upgrade bytes and identities', async () => {
  const baseline = await fixture('baseline');
  const upgrade = await fixture('upgrade', synthetic('0.1.1'));
  for (const f of [baseline, upgrade]) {
    const result = copy(f);
    assert.equal(result.status, 0, result.stderr || result.stdout);
    const source = await lstat(f.source, { bigint: true });
    const target = await lstat(f.target, { bigint: true });
    assert.equal(source.nlink, 1n);
    assert.equal(target.nlink, 1n);
    assert.ok(source.dev !== target.dev || source.ino !== target.ino);
    assert.equal(await readFile(f.target, 'utf8'), f.bytes);
    const check = verify(f);
    assert.equal(check.status, 0, check.stderr || check.stdout);
    assert.deepEqual(JSON.parse(check.stdout).roles, [
      { role: 'validated-intermediate', links: 1 },
      { role: 'published-package', links: 1 },
    ]);
  }
  await writeFile(baseline.source, synthetic('0.1.2'));
  await writeFile(upgrade.source, synthetic('0.1.3'));
  assert.equal(await readFile(baseline.target, 'utf8'), baseline.bytes);
  assert.equal(await readFile(upgrade.target, 'utf8'), upgrade.bytes);
  assert.notEqual(JSON.parse(baseline.bytes).productCode, JSON.parse(upgrade.bytes).productCode);
  assert.notEqual(JSON.parse(baseline.bytes).packageCode, JSON.parse(upgrade.bytes).packageCode);
});

for (const property of [
  'CreateHardLinksForCopyFilesToOutputDirectoryIfPossible',
  'CreateSymbolicLinksForCopyFilesToOutputDirectoryIfPossible',
]) {
  test(`actual WiX copy target refuses ${property}=true before writing output`, async () => {
    const f = await fixture(property);
    const result = copy(f, [`-p:${property}=true`]);
    assert.notEqual(result.status, 0);
    assert.match(result.stdout + result.stderr, /independent regular files/);
    await assert.rejects(lstat(f.target), { code: 'ENOENT' });
    assert.equal(await readFile(f.source, 'utf8'), f.bytes);
  });
}

test('publication check retains hardlink refusal and reports only bounded roles and counts', async () => {
  const f = await fixture('hardlink');
  await link(f.source, f.target);
  const check = verify(f);
  assert.notEqual(check.status, 0);
  assert.deepEqual(JSON.parse(check.stdout).roles, [
    { role: 'validated-intermediate', links: 2 },
    { role: 'published-package', links: 2 },
  ]);
  assert.ok(!check.stdout.includes(f.directory));
  assert.ok(!check.stderr.includes(f.directory));
  assert.equal(await readFile(f.source, 'utf8'), f.bytes);
});

test('publication check refuses symbolic aliases without adopting or rewriting them', async () => {
  const f = await fixture('symlink');
  await symlink(f.source, f.target);
  const check = verify(f);
  assert.notEqual(check.status, 0);
  assert.equal((await lstat(f.target)).isSymbolicLink(), true);
  assert.equal(await readFile(f.source, 'utf8'), f.bytes);
});

test('publication check refuses mismatched copied bytes and malformed receipt hashes', async () => {
  const f = await fixture('different-bytes');
  await writeFile(f.target, synthetic('0.1.1'));
  assert.notEqual(verify(f).status, 0);
  assert.notEqual(verify(f, 'NOT-A-RECEIPT-HASH').status, 0);
  assert.equal(await readFile(f.source, 'utf8'), f.bytes);
});

test('actual wrapper freshness guard refuses occupied directories and files without changing them', async () => {
  const f = await fixture('occupied-output');
  await writeFile(f.target, 'SYNTHETIC existing file; not adopted or overwritten');
  const result = spawnSync('pwsh', ['-NoProfile', '-Command', `
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PWD 'installer/windows/Build-Package.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Build wrapper did not parse.' }
$guards = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains('MSI publication requires a fresh empty output directory')
}, $true))
if ($guards.Count -ne 1) { throw 'Exactly one non-destructive output freshness guard is required.' }
$guard = [scriptblock]::Create($guards[0].Extent.Text)
foreach ($output in @($env:HERMES_MATERIALIZATION_DIRECTORY, $env:HERMES_MATERIALIZATION_FILE)) {
    $refused = $false
    try { & $guard } catch { $refused = $true }
    if (-not $refused) { throw 'Existing media was accepted for replacement.' }
}
$output = Join-Path $env:HERMES_MATERIALIZATION_DIRECTORY 'fresh-child'
& $guard
`], {
    encoding: 'utf8',
    env: { ...process.env, HERMES_MATERIALIZATION_DIRECTORY: f.output, HERMES_MATERIALIZATION_FILE: f.target },
  });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(await readFile(f.target, 'utf8'), 'SYNTHETIC existing file; not adopted or overwritten');
  const builder = await readFile(resolve('installer/windows/Build-Package.ps1'), 'utf8');
  assert.match(builder, /\$intermediate = Join-Path \$output 'obj'/);
  assert.ok(builder.includes('"-p:IntermediateOutputPath=$intermediate/"'));
  assert.ok(builder.includes('$receipt.packageSha256'));
});
