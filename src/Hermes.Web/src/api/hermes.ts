import type { CategoryCount, DocumentSummary, DocumentDetail, IndexStats, ReminderItem, Suggestion, TagCount } from '../types/hermes';

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

export async function batchDocuments(documentIds: number[], action: string, tag?: string): Promise<void> {
  await fetch(`${BASE}/api/documents/batch`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ documentIds, action, tag }),
  });
}
