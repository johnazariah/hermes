import type { BatchReclassificationResponse, CategoryCount, DocumentSummary, DocumentDetail, IndexStats, ReclassificationOutcome, ReminderItem, Suggestion, TagCount } from '../types/hermes';

const BASE = '';

export async function fetchCategories(): Promise<CategoryCount[]> {
  const res = await fetch(`${BASE}/api/categories`);
  return res.json();
}

export async function fetchDocuments(category: string, offset = 0, limit = 50): Promise<DocumentSummary[]> {
  const res = await fetch(`${BASE}/api/documents?category=${encodeURIComponent(category)}&offset=${offset}&limit=${limit}`);
  return res.json();
}

export async function fetchDocumentDetail(id: number): Promise<DocumentDetail> {
  const res = await fetch(`${BASE}/api/documents/${id}`);
  return res.json();
}

export async function fetchDocumentContent(id: number): Promise<{ markdown: string }> {
  const res = await fetch(`${BASE}/api/documents/${id}/content`);
  return res.json();
}

export async function fetchStats(): Promise<IndexStats> {
  const res = await fetch(`${BASE}/api/stats`);
  return res.json();
}

export async function fetchReminders(): Promise<ReminderItem[]> {
  const res = await fetch(`${BASE}/api/reminders`);
  return res.json();
}

export async function triggerSync(): Promise<void> {
  await fetch(`${BASE}/api/sync`, { method: 'POST' });
}

export async function fetchSuggestions(): Promise<Suggestion[]> {
  const res = await fetch(`${BASE}/api/suggestions`);
  return res.json();
}

export async function approveSuggestion(id: number): Promise<void> {
  await fetch(`${BASE}/api/suggestions/${id}/approve`, { method: 'POST' });
}

export async function rejectSuggestion(id: number): Promise<void> {
  await fetch(`${BASE}/api/suggestions/${id}/reject`, { method: 'POST' });
}

export async function fetchRecentDocuments(limit = 20): Promise<DocumentSummary[]> {
  const res = await fetch(`${BASE}/api/documents?limit=${limit}`);
  return res.json();
}

export async function fetchTags(): Promise<TagCount[]> {
  const res = await fetch(`${BASE}/api/tags`);
  if (!res.ok) {
    const cats = await fetchCategories();
    return cats.map(c => ({ tag: c.category, count: c.count }));
  }
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
  const res = await fetch(`${BASE}/api/documents?${qs}`);
  return res.json();
}

export async function addDocumentTag(docId: number, tag: string): Promise<void> {
  await fetch(`${BASE}/api/documents/${docId}/tags`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ tag }),
  });
}

export async function removeDocumentTag(docId: number, tag: string): Promise<void> {
  await fetch(`${BASE}/api/documents/${docId}/tags/${encodeURIComponent(tag)}`, {
    method: 'DELETE',
  });
}

export async function batchDocuments(documentIds: number[], action: "tag" | "star", value?: string): Promise<void> {
  const res = await fetch(`${BASE}/api/documents/batch`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ docIds: documentIds, action, value }),
  });
  if (!res.ok) throw new Error(`Batch ${action} failed`);
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
  const res = await fetch(`${BASE}/api/documents/batch`, {
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
