import type { AccessClient } from './access.ts';

export interface PreviewSource {
  documentId: number;
  originalName: string;
  filePath: string;
  instanceId: string;
  generation: number;
}

export function previewKind(name: string): 'pdf' | 'image' | undefined {
  return /\.pdf$/i.test(name) ? 'pdf' : /\.(png|jpe?g|gif|webp)$/i.test(name) ? 'image' : undefined;
}

function validFile(name: string, mime: string, bytes: Uint8Array): boolean {
  const starts = (...signature: number[]) => signature.every((value, index) => bytes[index] === value);
  if (/\.pdf$/i.test(name)) return mime === 'application/pdf' && starts(37, 80, 68, 70, 45);
  if (/\.png$/i.test(name)) return mime === 'image/png' && starts(137, 80, 78, 71, 13, 10, 26, 10);
  if (/\.jpe?g$/i.test(name)) return mime === 'image/jpeg' && starts(255, 216, 255);
  if (/\.gif$/i.test(name)) return mime === 'image/gif' && /^GIF8[79]a/.test(new TextDecoder().decode(bytes));
  return /\.webp$/i.test(name) && mime === 'image/webp' && starts(82, 73, 70, 70)
    && [87, 69, 66, 80].every((value, index) => bytes[index + 8] === value);
}

export async function loadDocumentPreview(
  client: Pick<AccessClient, 'request' | 'getSnapshot' | 'isCurrent' | 'subscribe'>,
  source: PreviewSource,
  signal: AbortSignal,
  urls: Pick<typeof URL, 'createObjectURL' | 'revokeObjectURL'> = URL,
): Promise<{ url: string; dispose: () => void }> {
  const current = () => {
    signal.throwIfAborted();
    if (!client.isCurrent(source.generation) || client.getSnapshot().instanceId !== source.instanceId) {
      throw new DOMException('Obsolete document preview', 'AbortError');
    }
  };
  current();
  const response = await client.request(`/api/documents/${source.documentId}/file`, { signal });
  const blob = await response.blob();
  current();
  const bytes = new Uint8Array(await blob.slice(0, 12).arrayBuffer());
  current();
  const mime = response.headers.get('content-type')?.split(';')[0].trim().toLowerCase() ?? '';
  if (!validFile(source.originalName, mime, bytes)) throw new Error('The original file has an unsupported or mismatched preview format.');
  const url = urls.createObjectURL(new Blob([blob], { type: mime }));
  let disposed = false;
  const dispose = () => {
    if (disposed) return;
    disposed = true;
    urls.revokeObjectURL(url);
    signal.removeEventListener('abort', dispose);
    unsubscribe();
  };
  const unsubscribe = client.subscribe(() => {
    if (!client.isCurrent(source.generation) || client.getSnapshot().instanceId !== source.instanceId) dispose();
  });
  signal.addEventListener('abort', dispose, { once: true });
  return { url, dispose };
}
