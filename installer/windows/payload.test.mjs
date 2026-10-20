import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { componentIdentity, generate, packageIdentity, upgradeCode, validate, validateVersion } from './payload.mjs';

// Synthetic controls remain in ignored project build output; no profile, installer or provider is accessed.
const runRoot = resolve('src/Hermes.Service/obj', `installer-controls-${randomUUID()}`);
const hash = content => createHash('sha256').update(content).digest('hex');
const identity = { productCode: '11111111-1111-1111-1111-111111111111', packageCode: '22222222-2222-2222-2222-222222222222', version: '0.1.0' };
const framework = spawnSync('pwsh', ['-NoProfile', '-Command', `
Add-Type -AssemblyName System.Reflection.Metadata
$path = [IO.Path]::Combine($PSHOME, 'System.Runtime.dll')
$stream = [IO.File]::OpenRead($path)
try {
    $image = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
        @{ path = $path; mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('D') } | ConvertTo-Json -Compress
    } finally { $image.Dispose() }
} finally { $stream.Dispose() }
`], { encoding: 'utf8' });
assert.equal(framework.status, 0, framework.stderr);
const managedFixture = JSON.parse(framework.stdout);
const managedBytes = await readFile(managedFixture.path);
let nativeBytes;
for (const candidate of [
  'src/Hermes.Service/bin/Release/net9.0/win-x64/Hermes.Service.exe',
  'tests/Hermes.Tray.Smoke/bin/Release/net9.0/win-x64/Hermes.Tray.Smoke.exe',
  'src/Hermes.Service/bin/Release/net9.0/runtimes/win-x64/native/e_sqlite3.dll',
  '.artifacts/installer-payload/service/Hermes.Service.exe',
]) {
  try {
    const bytes = await readFile(candidate);
    if (bytes.length <= 2 * 1024 * 1024 && bytes[0] === 0x4d && bytes[1] === 0x5a) { nativeBytes = bytes; break; }
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
}
assert.ok(nativeBytes, 'A genuine build-produced Windows PE fixture is required; no metadata controls are skipped.');
async function put(path, content = 'SYNTHETIC runtime fixture') {
  await mkdir(resolve(path, '..'), { recursive: true });
  await writeFile(path, content);
}
async function fixture(name) {
  const root = resolve(runRoot, name);
  const payload = resolve(root, 'payload');
  const inventory = resolve(root, 'inventory');
  const canonical = resolve(root, 'canonical');
  await put(resolve(canonical, 'index.html'), '<html><script type="module" src="/assets/app-AAAAAAAA.js"></script></html>');
  await put(resolve(canonical, 'assets/app-AAAAAAAA.js'), 'console.log("SYNTHETIC")');
  assert.equal(spawnSync(process.execPath, ['src/Hermes.Web/scripts/assets.mjs', 'create', canonical]).status, 0);
  const records = {};
  for (const [directory, part] of [['service', 'Service'], ['tray', 'Tray']]) {
    records[directory] = [];
    for (const name of [
      `Hermes.${part}.exe`, `Hermes.${part}.dll`, `Hermes.${part}.deps.json`, `Hermes.${part}.runtimeconfig.json`,
      'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Hermes.Servicing.dll',
      ...(part === 'Tray' ? ['System.Windows.Forms.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll']
        : ['Hermes.Core.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll',
          'prompts/comprehension.md', 'prompts/triage.md', 'prompts/deep/agent-statement.md',
          'prompts/deep/bank-statement.md', 'prompts/deep/payslip.md']),
    ]) {
      const source = name.startsWith('prompts/') ? resolve('src/Hermes.Core', name)
        : name === 'Hermes.Servicing.dll' ? resolve('src/Hermes.Servicing/obj', `${root.split(/[\\/]/).at(-1)}-${directory}-${randomUUID()}`, name)
          : resolve(root, 'sources', directory, name);
      if (!name.startsWith('prompts/')) {
        const bytes = name.endsWith('.exe') || name === 'coreclr.dll' ? nativeBytes : name.endsWith('.dll') ? managedBytes : 'SYNTHETIC runtime fixture';
        await put(source, bytes);
      }
      await put(resolve(payload, directory, name), await readFile(source));
      records[directory].push(`${source}|${name}|${hash(await readFile(source))}`);
    }
  }
  for (const name of ['index.html', 'assets/app-AAAAAAAA.js', 'asset-manifest.json']) {
    const source = resolve(canonical, name);
    await put(resolve(payload, 'service/wwwroot', name), await readFile(source));
    records.service.push(`${source}|wwwroot/${name}|${hash(await readFile(source))}`);
  }
  for (const part of ['service', 'tray']) await put(resolve(inventory, `${part}.publish.txt`), records[part].join('\n'));
  return { root, payload, inventory, canonical, records };
}

test('valid explicit SDK inventory accepts complete payload and generates stable HKCU components', async () => {
  const f = await fixture('valid');
  const files = await validate(f.payload, f.inventory, f.canonical);
  assert.ok(files.length > 20);
  assert.deepEqual(componentIdentity('service/Hermes.Service.dll'), componentIdentity('SERVICE/HERMES.SERVICE.DLL'));
  assert.notDeepEqual(componentIdentity('service/Hermes.Service.dll'), componentIdentity('tray/Hermes.Tray.dll'));
});
test('MSI version boundaries reject noncanonical and overflow fields', () => {
  for (const version of ['0.0.0', '255.255.65535']) assert.doesNotThrow(() => validateVersion(version));
  for (const version of ['256.0.0', '0.256.0', '0.0.65536', '01.0.0', '1.2.3.4', '9'.repeat(300) + '.0.0']) {
    assert.throws(() => validateVersion(version), /MSI version/);
  }
});

for (const path of ['service/archive/source.pdf', 'service/credentials.json', 'service/diagnostic.log', 'tray/config.yaml']) {
  test(`unexpected payload is rejected: ${path}`, async () => {
    const f = await fixture(path.replaceAll('/', '-'));
    await put(resolve(f.payload, path), 'SYNTHETIC not application-owned');
    await assert.rejects(validate(f.payload, f.inventory, f.canonical), /inventory|Unexpected/);
  });
}
test('forged inventory cannot legitimise an injected private-shaped file', async () => {
  const f = await fixture('forged');
  const source = resolve(f.root, 'sources/credentials.json');
  await put(source, 'SYNTHETIC fake credentials');
  await put(resolve(f.payload, 'service/credentials.json'), 'SYNTHETIC fake credentials');
  await put(resolve(f.inventory, 'service.publish.txt'), [...f.records.service, `${source}|credentials.json|${hash(await readFile(source))}`].join('\n'));
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /Unexpected SDK publish entry/);
});
test('runtime-essential assembly cannot be removed from both inventory and payload authority', async () => {
  const f = await fixture('missing-essential');
  await put(resolve(f.inventory, 'tray.publish.txt'), f.records.tray.filter(line => !line.includes('|Hermes.Tray.dll|')).join('\n'));
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /Runtime-essential/);
});
test('unexpected empty directories fail closed', async () => {
  const f = await fixture('empty-directory');
  await mkdir(resolve(f.payload, 'service/archive'), { recursive: true });
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /empty payload directory/);
});
test('modified runtime bytes fail without resigning output inventory', async () => {
  const f = await fixture('modified');
  await put(resolve(f.payload, 'service/Hermes.Service.dll'), 'SYNTHETIC changed output only');
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /differs from its SDK publish source/);
});
test('publish-time source hashes survive a second version overwriting intermediate build output', async () => {
  const f = await fixture('intermediate-overwritten');
  await put(resolve(f.root, 'sources/service/Hermes.Service.dll'), 'SYNTHETIC later version intermediate');
  assert.ok((await validate(f.payload, f.inventory, f.canonical)).length > 20);
  const manifest = await generate(f.payload, f.inventory, resolve(f.root, 'generated'), identity, f.canonical);
  assert.equal(manifest.files.find(file => file.path === 'service/Hermes.Service.dll').moduleVersionId, managedFixture.mvid);
});
test('forged web inventory cannot substitute bytes for the canonical build', async () => {
  const f = await fixture('forged-web');
  const injected = 'SYNTHETIC forged web output';
  await put(resolve(f.payload, 'service/wwwroot/index.html'), injected);
  await put(resolve(f.inventory, 'service.publish.txt'), f.records.service.map(line => line.includes('|wwwroot/index.html|')
    ? `${resolve(f.canonical, 'index.html')}|wwwroot/index.html|${hash(injected)}` : line).join('\n'));
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /Noncanonical web publish source/);
});
test('resigning an injected archive in the canonical manifest does not make it a runtime asset', async () => {
  const f = await fixture('resigned-canonical');
  await put(resolve(f.canonical, 'source.pdf'), 'SYNTHETIC archive document');
  assert.equal(spawnSync(process.execPath, ['src/Hermes.Web/scripts/assets.mjs', 'create', f.canonical]).status, 0);
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /outside the explicit React publish inventory/);
});
test('generated WiX has an HKCU keypath for every payload component', async () => {
  const f = await fixture('wix');
  const manifest = await generate(f.payload, f.inventory, resolve(f.root, 'generated'), identity, f.canonical);
  const files = manifest.files;
  const wxs = await readFile(resolve(f.root, 'generated/Payload.wxs'), 'utf8');
  assert.equal([...wxs.matchAll(/<Component Id=/g)].length, files.length);
  assert.equal([...wxs.matchAll(/Root="HKCU"[^>]*KeyPath="yes"/g)].length, files.length);
  assert.equal([...wxs.matchAll(/<File [^>]*KeyPath="no"/g)].length, files.length);
  assert.ok(!wxs.includes('<Files '));
  assert.ok(wxs.includes('<RemoveFolder '));
  assert.deepEqual(Object.keys(manifest).sort(), ['admissionAbi', 'files', 'packageCode', 'productCode', 'root', 'schemaVersion', 'scope', 'upgradeCode', 'version']);
  assert.equal(manifest.root, 'LocalApplicationData/Hermes/App');
  assert.equal(manifest.scope, 'perUser');
  assert.equal(manifest.admissionAbi, 1);
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.upgradeCode, upgradeCode);
  assert.deepEqual({ productCode: manifest.productCode, packageCode: manifest.packageCode, version: manifest.version }, identity);
  for (const file of files) {
    assert.deepEqual(Object.keys(file).sort(), ['componentCode', 'componentId', 'fileId', 'length', 'moduleVersionId', 'path', 'sha256']);
    const component = componentIdentity(file.path);
    assert.equal(file.componentId, component.id);
    assert.equal(file.componentCode, component.guid);
    assert.equal(file.fileId, `File_${component.id}`);
    assert.match(file.sha256, /^[a-f0-9]{64}$/);
    assert.ok(wxs.includes(`Id="${file.fileId}"`));
  }
  assert.equal(files.find(file => file.path === 'service/Hermes.Service.dll').moduleVersionId, managedFixture.mvid);
  assert.equal(files.find(file => file.path === 'tray/Hermes.Tray.dll').moduleVersionId, managedFixture.mvid);
  assert.equal(files.find(file => file.path === 'service/Hermes.Service.exe').moduleVersionId, null);
  assert.equal(files.find(file => file.path === 'service/coreclr.dll').moduleVersionId, null);
  assert.equal(files.find(file => file.path === 'service/wwwroot/index.html').moduleVersionId, null);
  assert.equal(files.filter(file => file.path.endsWith('/Hermes.Servicing.dll')).length, 2);
  const persisted = JSON.parse(await readFile(resolve(f.root, 'generated/payload-inventory.json'), 'utf8'));
  assert.deepEqual(persisted, manifest);
});

test('explicit build identity rejects empty, malformed, reused and noncanonical version values', () => {
  for (const patch of [
    { productCode: '{11111111-1111-1111-1111-111111111111' },
    { productCode: '11111111-1111-{1111}-1111-111111111111' },
    { productCode: '00000000-0000-0000-0000-000000000000' },
    { packageCode: identity.productCode }, { productCode: upgradeCode },
    { version: '1.2.3.4' }, { root: 'elsewhere' },
  ]) assert.throws(() => packageIdentity({ ...identity, ...patch }));
});

test('the added Servicing source root does not admit SDK-declared private-shaped extras', async () => {
  const f = await fixture('servicing-private');
  const source = resolve('src/Hermes.Servicing/obj', `private-control-${randomUUID()}`, 'credentials.json');
  const bytes = 'SYNTHETIC not application runtime';
  await put(source, bytes);
  await put(resolve(f.payload, 'service/credentials.json'), bytes);
  await put(resolve(f.inventory, 'service.publish.txt'), [...f.records.service, `${source}|credentials.json|${hash(bytes)}`].join('\n'));
  await assert.rejects(validate(f.payload, f.inventory, f.canonical), /Unexpected SDK publish entry/);
});

test('the PowerShell build identity writer preserves selected GUIDs and rejects reuse before generation', async () => {
  const path = resolve(runRoot, 'writer/build-identity.json');
  const argumentsFor = packageCode => ['-NoProfile', '-File', 'installer/windows/Write-BuildIdentity.ps1',
    '-ProductCode', identity.productCode, '-PackageCode', packageCode, '-Version', identity.version, '-OutputPath', path];
  const valid = spawnSync('pwsh', argumentsFor(identity.packageCode), { encoding: 'utf8' });
  assert.equal(valid.status, 0, valid.stderr);
  assert.deepEqual(JSON.parse(await readFile(path, 'utf8')), identity);
  const refused = spawnSync('pwsh', argumentsFor(identity.productCode), { encoding: 'utf8' });
  assert.notEqual(refused.status, 0);
  assert.deepEqual(JSON.parse(await readFile(path, 'utf8')), identity);
});

test('malformed PE remains an error even when its synthetic SDK source hash matches', async () => {
  const f = await fixture('malformed-pe');
  const source = resolve(f.root, 'sources/service/Hermes.Service.dll');
  const bytes = Buffer.from('MZ deliberately malformed synthetic PE');
  await put(source, bytes);
  await put(resolve(f.payload, 'service/Hermes.Service.dll'), bytes);
  await put(resolve(f.inventory, 'service.publish.txt'), f.records.service.map(line => line.includes('|Hermes.Service.dll|')
    ? `${source}|Hermes.Service.dll|${hash(bytes)}` : line).join('\n'));
  await assert.rejects(generate(f.payload, f.inventory, resolve(f.root, 'generated'), identity, f.canonical), /PE metadata verification failed/);
});

test('metadata reader refuses bytes changed after the provenance snapshot', async () => {
  const f = await fixture('metadata-race');
  const files = await validate(f.payload, f.inventory, f.canonical);
  const request = resolve(f.root, 'validated-publish.json');
  await put(request, JSON.stringify({ files }));
  await put(resolve(f.payload, 'service/Hermes.Service.dll'), nativeBytes);
  const result = spawnSync('pwsh', ['-NoProfile', '-File', 'installer/windows/Read-PayloadMetadata.ps1',
    '-PayloadDirectory', f.payload, '-InventoryPath', request, '-OutputPath', resolve(f.root, 'metadata.json')], { encoding: 'utf8' });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /changed after SDK\/canonical provenance/);
});
