import type { CategoryCount, DocumentSummary, DocumentDetail, IndexStats, ReminderItem, Suggestion } from '../types/hermes';

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
