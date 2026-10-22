import type { BatchReclassificationResponse, CategoryCount, DocumentSummary, DocumentDetail, IndexStats, ReclassificationOutcome, ReminderItem, Suggestion, TagCount } from '../types/hermes';
import { request } from './http.ts';
import { isChatSource, type ChatSource } from './chat.ts';

const BASE = '';

export async function fetchCategories(): Promise<CategoryCount[]> {
  const res = await request(`${BASE}/api/categories`);
  return res.json();
}

export async function fetchDocuments(category: string, offset = 0, limit = 50): Promise<DocumentSummary[]> {
  const res = await request(`${BASE}/api/documents?category=${encodeURIComponent(category)}&offset=${offset}&limit=${limit}`);
  return res.json();
}

export async function fetchDocumentDetail(id: number, signal?: AbortSignal): Promise<DocumentDetail> {
  const res = await request(`${BASE}/api/documents/${id}`, { signal });
  return res.json();
}

export async function fetchDocumentContent(id: number, signal?: AbortSignal): Promise<{ markdown: string }> {
  const res = await request(`${BASE}/api/documents/${id}/content`, { signal });
  return res.json();
}

export async function fetchStats(): Promise<IndexStats> {
  const res = await request(`${BASE}/api/stats`);
  return res.json();
}

export async function fetchReminders(): Promise<ReminderItem[]> {
  const res = await request(`${BASE}/api/reminders`);
  return res.json();
}

export async function triggerSync(): Promise<void> {
  const response = await request(`${BASE}/api/sync`, { method: 'POST' });
  if (response.status !== 202) throw new Error('The service did not accept a sync request.');
  const body: unknown = await response.json();
  if (typeof body !== 'object' || body === null || !('status' in body)
    || (body.status !== 'queued' && body.status !== 'coalesced')) throw new Error('The service did not acknowledge the sync request.');
}

export async function fetchSuggestions(): Promise<Suggestion[]> {
  const res = await request(`${BASE}/api/suggestions`);
  return res.json();
}

export async function approveSuggestion(id: number): Promise<void> {
  await request(`${BASE}/api/suggestions/${id}/approve`, { method: 'POST' });
}

export async function rejectSuggestion(id: number): Promise<void> {
  await request(`${BASE}/api/suggestions/${id}/reject`, { method: 'POST' });
}

export async function fetchRecentDocuments(limit = 20): Promise<DocumentSummary[]> {
  const res = await request(`${BASE}/api/documents?limit=${limit}`);
  return res.json();
}

export async function fetchTags(): Promise<TagCount[]> {
  const res = await request(`${BASE}/api/tags`);
  return res.json();
}

export async function searchDocuments(params: {
  q?: string;
  category?: string;
  limit?: number;
}): Promise<DocumentSummary[]> {
  const qs = new URLSearchParams();
  if (params.q) qs.set('q', params.q);
  if (params.category) qs.set('category', params.category);
  qs.set('limit', String(params.limit ?? 50));
  const res = await request(`${BASE}/api/documents?${qs}`);
  return res.json();
}

export async function searchWithModel(q: string, category: string, mode: 'semantic' | 'hybrid'): Promise<ChatSource[]> {
  const params = new URLSearchParams({ q, mode, limit: '50' });
  if (category) params.set('category', category);
  const rows: unknown = await (await request(`/api/search?${params}`, {}, { capabilities: ['model.invoke'] })).json();
  if (!Array.isArray(rows)) throw new Error('The Service returned invalid search results.');
  return rows.map((row: Record<string, unknown>) => {
    const result = {
      documentId: row.id, savedPath: row.path, resultType: row.resultType, originalName: row.originalName,
      category: row.category, sender: row.sender, subject: row.subject, snippet: row.snippet, extractedAmount: row.amount,
    };
    if (!isChatSource(result)) throw new Error('The Service returned an invalid search source.');
    return result;
  });
}

export async function addDocumentTag(docId: number, tag: string): Promise<void> {
  await request(`${BASE}/api/documents/${docId}/tags`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tags: [tag] }),
  });
  await verifyDocumentTag(docId, tag);
}

async function verifyDocumentTag(docId: number, tag: string): Promise<void> {
  const rows: unknown = await (await request(`/api/documents/${docId}/tags`)).json();
  if (!Array.isArray(rows) || !rows.some(row => typeof row === 'object' && row !== null && row.tag === tag)) {
    throw new Error(`The Service did not persist the tag for document ${docId}.`);
  }
}

export async function removeDocumentTag(docId: number, tag: string): Promise<void> {
  await request(`${BASE}/api/documents/${docId}/tags/${encodeURIComponent(tag)}`, {
    method: 'DELETE',
  });
}

export async function batchDocuments(documentIds: number[], action: "tag" | "star", value?: string): Promise<void> {
  const response = await request(`${BASE}/api/documents/batch`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ docIds: documentIds, action, value }),
  });
  const body: unknown = await response.json();
  if (typeof body !== 'object' || body === null || !('action' in body) || body.action !== action
    || !('count' in body) || body.count !== documentIds.length) throw new Error('The Service did not confirm the batch action.');
  if (action === 'tag') {
    for (const id of documentIds) await verifyDocumentTag(id, value ?? '');
  }
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isReclassificationOutcome(value: unknown): value is ReclassificationOutcome {
  if (typeof value !== 'object' || value === null) return false;
  const outcome = value as Record<string, unknown>;
  return (
    typeof outcome.documentId === 'number'
    && (outcome.status === 'reclassified' || outcome.status === 'unchanged' || outcome.status === 'failed')
    && isNullableString(outcome.previousCategory)
    && isNullableString(outcome.category)
    && typeof outcome.changed === 'boolean'
    && isNullableString(outcome.savedPath)
    && isNullableString(outcome.sha256)
    && isNullableString(outcome.error)
  );
}

export async function reclassifyDocuments(
  documentIds: number[],
  category: string,
): Promise<BatchReclassificationResponse> {
  const res = await request(`${BASE}/api/documents/batch`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      docIds: documentIds,
      action: 'reclassify',
      value: category,
    }),
  });
  if (!res.ok) {
    let detail = '';
    try {
      const body = await res.json() as {
        detail?: string;
        error?: string;
        message?: string;
        title?: string;
      };
      detail = body.detail ?? body.error ?? body.message ?? body.title ?? '';
    } catch {
      // The status code remains available when the response has no JSON error body.
    }
    const status = `HTTP ${res.status}`;
    throw new Error(detail ? `${detail} (${status})` : `Reclassification request failed (${status})`);
  }

  const body: unknown = await res.json();
  if (
    typeof body === 'object'
    && body !== null
    && 'error' in body
    && typeof body.error === 'string'
  ) {
    throw new Error(body.error);
  }
  if (
    typeof body !== 'object'
    || body === null
    || !('action' in body)
    || body.action !== 'reclassify'
    || !('succeeded' in body)
    || typeof body.succeeded !== 'number'
    || !('unchanged' in body)
    || typeof body.unchanged !== 'number'
    || !('failed' in body)
    || typeof body.failed !== 'number'
    || !('outcomes' in body)
    || !Array.isArray(body.outcomes)
    || !body.outcomes.every(isReclassificationOutcome)
  ) {
    throw new Error('The service returned an invalid reclassification response.');
  }
  return body as BatchReclassificationResponse;
}
