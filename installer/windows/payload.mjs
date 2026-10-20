import { createHash } from 'node:crypto';
import { readFile, readdir, lstat, realpath, mkdir, writeFile } from 'node:fs/promises';
import { resolve, relative, dirname, basename, sep } from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const upgradeCode = '7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A';
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const normal = name => name.split(sep).join('/');
const within = (root, path) => {
  const name = relative(root, path);
  return name !== '' && name !== '..' && !name.startsWith(`..${sep}`) && !name.startsWith(sep);
};
const safeRelative = name => /^[A-Za-z0-9_@.+/-]+$/.test(name)
  && !name.split('/').some(part => part === '' || part === '.' || part === '..');
const promptFiles = [
  'prompts/comprehension.md', 'prompts/triage.md', 'prompts/deep/agent-statement.md',
  'prompts/deep/bank-statement.md', 'prompts/deep/payslip.md',
];
const required = part => [
  `Hermes.${part}.exe`, `Hermes.${part}.dll`, `Hermes.${part}.deps.json`,
  `Hermes.${part}.runtimeconfig.json`, 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
  'System.Private.CoreLib.dll',
  ...(part === 'Tray' ? ['System.Windows.Forms.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll']
    : ['Hermes.Core.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', ...promptFiles]),
];

export function validateVersion(version) {
  if (!/^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/.test(version)) {
    throw Error('MSI version must have three canonical numeric fields.');
  }
  if (version.split('.').map(BigInt).some((part, index) => part > [255n, 255n, 65535n][index])) {
    throw Error('MSI version exceeds 255.255.65535.');
  }
}

async function tree(root, directory = root) {
  if (!(await lstat(directory)).isDirectory() || (await lstat(directory)).isSymbolicLink()) {
    throw Error('Payload and canonical build directories must not be links.');
  }
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isSymbolicLink() || (await lstat(path)).isSymbolicLink()) throw Error('Payload links are forbidden.');
    if (entry.isDirectory()) {
      const children = await tree(root, path);
      if (!children.length) throw Error(`Unexpected empty payload directory: ${normal(relative(root, path))}`);
      result.push(...children);
    } else if (entry.isFile()) result.push(normal(relative(root, path)));
    else throw Error('Payload contains a non-regular file.');
  }
  return result.sort();
}

function runtimeName(name) {
  return /^[A-Za-z0-9_.+-]+\.(dll|exe|pdb|xml)$/.test(name)
    || /^Hermes\.(Service|Tray)\.(deps|runtimeconfig)\.json$/.test(name)
    || /^Hermes\.Service\.staticwebassets\.endpoints\.json$/.test(name)
    || /^[a-z]{2}(?:-[A-Za-z0-9]+)?\/[A-Za-z0-9_.+-]+\.resources\.dll$/.test(name)
    || /^runtimes\/win-(x64|x86|arm64)\/native\/[A-Za-z0-9_.+-]+\.dll$/.test(name);
}

export async function validate(payload, inventories, canonical = resolve(repository, 'src/Hermes.Web/dist')) {
  payload = resolve(payload);
  inventories = resolve(inventories);
  if (inventories === payload || within(payload, inventories)) throw Error('Publish inventories must be outside the untrusted payload.');
  if (!(await lstat(inventories)).isDirectory() || (await lstat(inventories)).isSymbolicLink()) {
    throw Error('Publish inventory directory must not be a link.');
  }
  const verifier = spawnSync(process.execPath, [resolve(repository, 'src/Hermes.Web/scripts/assets.mjs'), 'verify', canonical], { encoding: 'utf8' });
  if (verifier.status !== 0) throw Error('Trusted canonical React build did not verify.');
  const canonicalFiles = await tree(canonical);
  if (canonicalFiles.some(name => !/^(index\.html|asset-manifest\.json|favicon\.svg|icons\.svg|assets\/[A-Za-z0-9_-]+-[A-Za-z0-9_-]{8}\.(js|css))$/.test(name))) {
    throw Error('Canonical build contains a file outside the explicit React publish inventory.');
  }
  const expected = new Map();
  const sources = new Set();
  for (const [directory, part] of [['service', 'Service'], ['tray', 'Tray']]) {
    const inventory = resolve(inventories, `${directory}.publish.txt`);
    if (!(await lstat(inventory)).isFile() || (await lstat(inventory)).isSymbolicLink()) throw Error('Publish inventory must be a regular file.');
    const lines = (await readFile(inventory, 'utf8')).replace(/^\uFEFF/, '').trim().split(/\r?\n/);
    for (const line of lines) {
      const pair = line.split('|');
      if (pair.length !== 3 || !/^[0-9a-f]{64}$/i.test(pair[2])) throw Error('Malformed SDK publish inventory.');
      const source = resolve(pair[0]);
      const realSource = await realpath(source);
      const name = pair[1].replaceAll('\\', '/');
      const key = `${directory}/${name}`;
      if (!safeRelative(name) || expected.has(key.toLowerCase())) throw Error(`Invalid or duplicate publish identity: ${key}`);
      if (name.startsWith('wwwroot/')) {
        if (directory !== 'service' || !canonicalFiles.includes(name.slice(8))
          || resolve(canonical, name.slice(8)) !== source
          || sha(await readFile(source)) !== pair[2].toLowerCase()) throw Error(`Noncanonical web publish source: ${key}`);
      } else if (directory === 'service' && promptFiles.includes(name)) {
        if (source !== resolve(repository, 'src/Hermes.Core', name)) throw Error(`Unexpected prompt source: ${key}`);
      } else {
        const buildRoots = ['Hermes.Service', 'Hermes.Core', 'Hermes.Tray', 'Hermes.Servicing'].flatMap(project =>
          ['bin', 'obj'].map(folder => resolve(repository, 'src', project, folder)));
        const packageRoot = resolve(process.env.NUGET_PACKAGES || resolve(homedir(), '.nuget/packages'));
        const sdkPacks = process.env.DOTNET_ROOT ? [resolve(process.env.DOTNET_ROOT, 'packs')] : [];
        if (!runtimeName(name) || ![packageRoot, ...sdkPacks, ...buildRoots].some(root => within(root, source) && within(root, realSource))) {
          throw Error(`Unexpected SDK publish entry: ${key}`);
        }
      }
      if (!(await lstat(source)).isFile() || (await lstat(source)).isSymbolicLink()) throw Error('Publish source must be a regular file.');
      // Hashes were captured from SDK-resolved sources at publish time, before another
      // version can overwrite intermediate compiler outputs. Never sign the candidate itself.
      expected.set(key.toLowerCase(), { path: key, sha256: pair[2].toLowerCase() });
      sources.add(key);
    }
    for (const name of required(part)) {
      if (!sources.has(`${directory}/${name}`)) throw Error(`Runtime-essential SDK publish entry missing: ${directory}/${name}`);
    }
  }
  for (const name of canonicalFiles) {
    if (!sources.has(`service/wwwroot/${name}`)) throw Error(`Canonical React publish entry missing: ${name}`);
  }
  const actual = await tree(payload);
  if (actual.length !== expected.size) throw Error('Payload file count differs from the SDK publish inventory.');
  for (const name of actual) {
    const item = expected.get(name.toLowerCase());
    if (!item || item.path !== name) throw Error(`Unexpected payload file: ${name}`);
    const bytes = await readFile(resolve(payload, name));
    if (sha(bytes) !== item.sha256) throw Error(`Payload differs from its SDK publish source: ${name}`);
    item.length = bytes.length;
  }
  return [...expected.values()].sort((a, b) => a.path.localeCompare(b.path, 'en'));
}

const xml = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
export function componentIdentity(path) {
  const hash = sha(path.toLowerCase());
  // Component identity depends only on the stable relative path, never build roots or bytes.
  const guid = `${hash.slice(0, 8)}-${hash.slice(8, 12)}-5${hash.slice(13, 16)}-a${hash.slice(17, 20)}-${hash.slice(20, 32)}`;
  return { id: `Payload_${hash.slice(0, 32)}`, guid: guid.toUpperCase() };
}

export function packageIdentity(identity) {
  if (!identity || Object.keys(identity).sort().join(',') !== 'packageCode,productCode,version') throw Error('Explicit build identity requires only productCode, packageCode and version.');
  validateVersion(identity.version);
  const guid = value => {
    if (typeof value !== 'string' || value.startsWith('{') !== value.endsWith('}')) throw Error('Invalid build GUID.');
    const result = (value.startsWith('{') ? value.slice(1, -1) : value).toUpperCase();
    if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(result)) throw Error('Invalid build GUID.');
    if (result === '00000000-0000-0000-0000-000000000000') throw Error('Build GUID must not be empty.');
    return result;
  };
  const productCode = guid(identity.productCode);
  const packageCode = guid(identity.packageCode);
  if (productCode === packageCode || [productCode, packageCode].includes(upgradeCode)) throw Error('Product, package and upgrade identities must differ.');
  return { productCode, packageCode, version: identity.version };
}

export async function generate(payload, inventories, output, identity, canonical) {
  if (resolve(output) === resolve(payload) || within(resolve(payload), resolve(output))) {
    throw Error('Generated packaging files must be outside the untrusted payload.');
  }
  const selected = packageIdentity(identity);
  const files = await validate(payload, inventories, canonical);
  await mkdir(output, { recursive: true });
  const request = resolve(output, 'validated-publish.json');
  const metadataPath = resolve(output, 'payload-metadata.json');
  await writeFile(request, `${JSON.stringify({ files })}\n`);
  const metadataResult = spawnSync('pwsh', ['-NoProfile', '-File', resolve(repository, 'installer/windows/Read-PayloadMetadata.ps1'),
    '-PayloadDirectory', resolve(payload), '-InventoryPath', request, '-OutputPath', metadataPath], { encoding: 'utf8', maxBuffer: 1024 * 1024 });
  if (metadataResult.status !== 0) throw Error(`PE metadata verification failed: ${metadataResult.stderr || 'metadata reader did not complete'}`);
  const metadata = JSON.parse(await readFile(metadataPath, 'utf8'));
  if (!Array.isArray(metadata.files) || metadata.files.length !== files.length) throw Error('Incomplete payload module metadata.');
  const modules = new Map();
  for (const file of metadata.files) {
    if (modules.has(file.path) || !Object.hasOwn(file, 'moduleVersionId')
      || (file.moduleVersionId !== null && (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(file.moduleVersionId)
        || file.moduleVersionId === '00000000-0000-0000-0000-000000000000'))) throw Error('Invalid payload module metadata.');
    modules.set(file.path, file);
  }
  const manifestFiles = files.map(file => {
    const module = modules.get(file.path);
    if (!module || module.sha256 !== file.sha256 || module.length !== file.length) throw Error('Payload module metadata differs from validated provenance.');
    const component = componentIdentity(file.path);
    return { ...file, moduleVersionId: module.moduleVersionId, fileId: `File_${component.id}`, componentId: component.id, componentCode: component.guid };
  });
  for (const entry of ['service/Hermes.Service.dll', 'tray/Hermes.Tray.dll']) {
    if (!manifestFiles.find(file => file.path === entry)?.moduleVersionId) throw Error(`Managed runtime entry MVID missing: ${entry}`);
  }
  const manifest = { schemaVersion: 1, admissionAbi: 1, root: 'LocalApplicationData/Hermes/App', scope: 'perUser',
    upgradeCode, ...selected, files: manifestFiles };
  const directories = new Set(files.flatMap(file => {
    const parts = file.path.split('/').slice(0, -1);
    return parts.map((_, index) => parts.slice(0, index + 1).join('/'));
  }));
  const directoryId = path => `Dir_${sha(path.toLowerCase()).slice(0, 32)}`;
  const directoryXml = [...directories].sort().map(path => {
    const parent = normal(dirname(path));
    return `<DirectoryRef Id="${parent === '.' ? 'INSTALLFOLDER' : directoryId(parent)}"><Directory Id="${directoryId(path)}" Name="${xml(basename(path))}" /></DirectoryRef>`;
  }).join('\n');
  const removedDirectories = new Set();
  const components = files.map(file => {
    const { id, guid } = componentIdentity(file.path);
    const directory = normal(dirname(file.path));
    // Ancestors without direct files still need empty-folder-only MSI ownership (ICE64).
    const parts = directory.split('/');
    const remove = parts.map((_, index) => parts.slice(0, index + 1).join('/'))
      .filter(path => !removedDirectories.has(path))
      .map(path => {
        removedDirectories.add(path);
        return `<RemoveFolder Id="Remove_${directoryId(path)}" Directory="${directoryId(path)}" On="uninstall" />`;
      }).join('\n');
    return `<Component Id="${id}" Guid="${guid}" Directory="${directoryId(normal(dirname(file.path)))}">
      <File Id="File_${id}" Name="${xml(basename(file.path))}" Source="${xml(resolve(payload, file.path))}" KeyPath="no" />
      <RegistryValue Root="HKCU" Key="Software\\Hermes\\Installer\\Components" Name="${id}" Type="integer" Value="1" KeyPath="yes" />
      ${remove}
    </Component>`;
  }).join('\n');
  await writeFile(resolve(output, 'Payload.wxs'), `<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment>${directoryXml}
<ComponentGroup Id="ProductComponents">${components}</ComponentGroup></Fragment></Wix>\n`);
  await writeFile(resolve(output, 'payload-inventory.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [action, payload, inventories, output, identityPath] = process.argv.slice(2);
  if (!['verify', 'generate'].includes(action) || !payload || !inventories || (action === 'generate' && (!output || !identityPath))) {
    throw Error('Usage: node installer/windows/payload.mjs verify|generate <payload> <SDK-inventories> [generated-output build-identity.json]');
  }
  const result = action === 'generate' ? await generate(resolve(payload), resolve(inventories), resolve(output), JSON.parse(await readFile(identityPath, 'utf8')))
    : await validate(payload, inventories);
  console.log(`Verified explicit Windows publish inventory (${action === 'generate' ? result.files.length : result.length} runtime files).`);
}
