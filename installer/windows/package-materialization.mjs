import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { lstat } from 'node:fs/promises';

async function hash(path) {
  const digest = createHash('sha256');
  for await (const bytes of createReadStream(path)) digest.update(bytes);
  return digest.digest('hex');
}

try {
  const [intermediate, output, expectedHash] = process.argv.slice(2);
  if (process.argv.length !== 5 || !/^[0-9a-f]{64}$/.test(expectedHash)) throw Error();
  const paths = [intermediate, output];
  const identities = await Promise.all(paths.map(path => lstat(path, { bigint: true }).catch(() => null)));
  console.log(JSON.stringify({
    schemaVersion: 1,
    roles: identities.map((identity, index) => ({
      role: ['validated-intermediate', 'published-package'][index],
      links: identity === null ? null : Number(identity.nlink),
    })),
  }));
  if (identities.some(identity => identity === null || !identity.isFile()
    || identity.isSymbolicLink() || identity.nlink !== 1n)) throw Error();
  if (identities[0].dev === identities[1].dev && identities[0].ino === identities[1].ino) throw Error();
  for (const path of paths) {
    if (await hash(path) !== expectedHash) throw Error();
  }
} catch {
  console.error('Package materialisation refused: independent single-link regular files must retain the validated MSI hash.');
  process.exitCode = 1;
}
