import { request } from './http.ts';

export interface ChatSource {
  documentId: number;
  resultType: 'document' | 'email';
  savedPath: string;
  originalName: string | null;
  subject: string | null;
  category: string | null;
  extractedAmount: number | null;
  sender: string | null;
  snippet: string | null;
}

export type ChatEvent =
  | { type: 'results'; results: ChatSource[] }
  | { type: 'answer'; answer: string }
  | { type: 'done' };

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function nullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

export function isChatSource(value: unknown): value is ChatSource {
  return record(value) && typeof value.documentId === 'number'
    && Number.isSafeInteger(value.documentId) && value.documentId > 0
    && (value.resultType === 'document' || value.resultType === 'email')
    && typeof value.savedPath === 'string'
    && nullableString(value.subject)
    && nullableString(value.originalName) && nullableString(value.category)
    && nullableString(value.sender) && nullableString(value.snippet)
    && (value.extractedAmount === null || typeof value.extractedAmount === 'number');
}

export function sourceTitle(source: ChatSource): string {
  return [source.originalName, source.subject].find(value => value?.trim())
    ?? `${source.resultType === 'email' ? 'Email' : 'Document'} ${source.documentId}`;
}

function decodeFrame(frame: string): ChatEvent | null {
  const lines = frame.split(/\r?\n/);
  const type = lines.find(line => line.startsWith('event:'))?.slice(6).trim();
  if (!type || !['results', 'answer', 'done'].includes(type)) return null;
  const data = lines.filter(line => line.startsWith('data:'))
    .map(line => line.slice(5).replace(/^ /, '')).join('\n');
  const value: unknown = JSON.parse(data);
  if (type === 'done' && record(value)) return { type: 'done' };
  if (type === 'results' && record(value)
    && Array.isArray(value.results) && value.results.every(isChatSource)) {
    return { type: 'results', results: value.results };
  }
  if (type === 'answer' && record(value) && typeof value.answer === 'string') {
    return { type: 'answer', answer: value.answer };
  }
  throw new Error(`Invalid chat ${type} event`);
}

export async function readChat(
  body: ReadableStream<Uint8Array>,
  receive: (event: ChatEvent) => void,
): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let buffer = '';
  let completed = false;
  let receivedResults = false;
  try {
    while (!completed) {
      const { done, value } = await reader.read();
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      if (buffer.length > 1_048_576) throw new Error('Chat event exceeds the size limit');
      let delimiter = /\r?\n\r?\n/.exec(buffer);
      while (delimiter && !completed) {
        const event = decodeFrame(buffer.slice(0, delimiter.index));
        buffer = buffer.slice(delimiter.index + delimiter[0].length);
        if (event) {
          if (event.type === 'results') receivedResults = true;
          if (event.type === 'done' && !receivedResults) {
            throw new Error('Chat completed without search results');
          }
          receive(event);
          completed = event.type === 'done';
        }
        delimiter = /\r?\n\r?\n/.exec(buffer);
      }
      if (done && !completed) throw new Error('Chat stream ended before its done event');
    }
  } finally {
    try { await reader.cancel(); } finally { reader.releaseLock(); }
  }
}

export async function chat(
  query: string,
  receive: (event: ChatEvent) => void,
  signal: AbortSignal,
  aiEnabled = false,
): Promise<void> {
  const response = await request('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, aiEnabled }),
    signal,
  }, { searchOnly: !aiEnabled });
  if (!response.ok) throw new Error(`Chat request failed (HTTP ${response.status})`);
  if (!response.headers.get('content-type')?.startsWith('text/event-stream') || !response.body) {
    throw new Error('The service did not return a chat event stream');
  }
  await readChat(response.body, receive);
}
