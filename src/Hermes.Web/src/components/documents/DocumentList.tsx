import { useState, useMemo, useCallback } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchDocuments, fetchCategories } from '../../api/hermes';
import type { DocumentSummary } from '../../types/hermes';

type GroupBy = 'none' | 'vendor' | 'sender' | 'date';
type SortBy = 'date' | 'amount' | 'name';

function extractSender(s: string | null): string {
  if (!s) return 'Unknown';
  const match = s.match(/^([^<]+)/);
  return match ? match[1].trim() : s;
}

function extractMonth(d: string | null): string {
  if (!d) return 'No date';
  // Try to parse various date formats into YYYY-MM
  const m = d.match(/(\d{4})-(\d{2})/) || d.match(/(\d{2})\/(\d{2})\/(\d{4})/);
  if (m && m.length >= 3) {
    return m[3] ? `${m[3]}-${m[2]}` : `${m[1]}-${m[2]}`;
  }
  return d;
}

function isCleanVendor(v: string | null | undefined): v is string {
  if (!v || v.length > 60 || v.length < 2) return false;
  const junk = /your |bank account|financial|institution|credit\/debit|click here|page \d|http/i;
  return !junk.test(v);
}

function bestVendorName(doc: DocumentSummary): string {
  if (isCleanVendor(doc.vendor)) return doc.vendor;
  return extractSender(doc.sender);
}

function groupDocs(docs: DocumentSummary[], groupBy: GroupBy): Map<string, DocumentSummary[]> {
  if (groupBy === 'none') return new Map([['All documents', docs]]);
  const groups = new Map<string, DocumentSummary[]>();
  for (const doc of docs) {
    const key = groupBy === 'vendor' ? bestVendorName(doc)
              : groupBy === 'sender' ? extractSender(doc.sender)
              : extractMonth(doc.extractedDate);
    const list = groups.get(key) || [];
    list.push(doc);
    groups.set(key, list);
  }
  return new Map([...groups.entries()].sort((a, b) => b[1].length - a[1].length));
}

function sortDocs(docs: DocumentSummary[], sortBy: SortBy): DocumentSummary[] {
  return [...docs].sort((a, b) => {
    if (sortBy === 'amount') return (b.extractedAmount ?? 0) - (a.extractedAmount ?? 0);
    if (sortBy === 'name') return a.originalName.localeCompare(b.originalName);
    return (b.extractedDate ?? '').localeCompare(a.extractedDate ?? '');
  });
}

export function DocumentList({ category, onSelectDocument }: {
  category: string;
  onSelectDocument: (id: number) => void;
}) {
  const queryClient = useQueryClient();
  const [groupBy, setGroupBy] = useState<GroupBy>('vendor');
  const [sortBy, setSortBy] = useState<SortBy>('date');
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; docId: number } | null>(null);
  const { data: docs, isLoading } = useQuery<DocumentSummary[]>({
    queryKey: ['documents', category],
    queryFn: () => fetchDocuments(category, 0, 500),
  });
  const { data: categories } = useQuery({ queryKey: ['categories'], queryFn: fetchCategories });

  const groups = useMemo(() => {
    if (!docs) return new Map<string, DocumentSummary[]>();
    return groupDocs(docs, groupBy);
  }, [docs, groupBy]);

  const toggleSelect = useCallback((id: number) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }, []);

  const selectAll = useCallback(() => {
    if (!docs) return;
    setSelected(new Set(docs.map(d => d.id)));
  }, [docs]);

  const batchAction = useCallback(async (action: string, value: string) => {
    const docIds = [...selected];
    if (docIds.length === 0) return;
    await fetch('/api/documents/batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ docIds, action, value }),
    });
    setSelected(new Set());
    queryClient.invalidateQueries({ queryKey: ['documents'] });
    queryClient.invalidateQueries({ queryKey: ['categories'] });
  }, [selected, queryClient]);

  const handleContextMenu = useCallback((e: React.MouseEvent, docId: number) => {
    e.preventDefault();
    setContextMenu({ x: e.clientX, y: e.clientY, docId });
    if (!selected.has(docId)) setSelected(new Set([docId]));
  }, [selected]);

  const moveToCategory = useCallback(async (cat: string) => {
    const docIds = contextMenu ? [...selected] : [];
    if (docIds.length === 0) return;
    await fetch('/api/documents/batch', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ docIds, action: 'move', value: cat }),
    });
    setSelected(new Set());
    setContextMenu(null);
    queryClient.invalidateQueries({ queryKey: ['documents'] });
    queryClient.invalidateQueries({ queryKey: ['categories'] });
  }, [contextMenu, selected, queryClient]);

  if (isLoading) return <div className="text-neutral-500 text-sm">Loading…</div>;

  return (
    <div onClick={() => setContextMenu(null)}>
      <div className="flex items-center gap-4 mb-4">
        <h2 className="text-lg font-semibold">📁 {category}</h2>
        <span className="text-xs text-neutral-500">({docs?.length ?? 0})</span>
      </div>

      {/* Selection toolbar */}
      {selected.size > 0 && (
        <div className="flex items-center gap-3 mb-3 px-3 py-2 bg-blue-950/30 border border-blue-900/50 rounded-lg text-sm">
          <span className="text-blue-300">{selected.size} selected</span>
          <button onClick={selectAll} className="text-xs text-neutral-400 hover:text-neutral-200">Select all</button>
          <button onClick={() => setSelected(new Set())} className="text-xs text-neutral-400 hover:text-neutral-200">Clear</button>
          <span className="border-l border-neutral-700 h-4 mx-1" />
          <span className="text-xs text-neutral-400">Move to:</span>
          {categories?.filter(c => c.category !== category).slice(0, 5).map(c => (
            <button key={c.category} onClick={() => batchAction('move', c.category)}
                    className="text-xs px-2 py-0.5 bg-neutral-800 rounded hover:bg-neutral-700">{c.category}</button>
          ))}
          <button onClick={() => batchAction('star', '')} className="text-xs text-neutral-400 hover:text-neutral-200 ml-auto">⭐ Star</button>
        </div>
      )}

      {/* Controls */}
      <div className="flex gap-3 mb-4 text-xs">
        <label className="flex items-center gap-1 text-neutral-400">
          Group:
          <select value={groupBy} onChange={e => setGroupBy(e.target.value as GroupBy)}
                  className="bg-neutral-800 border border-neutral-700 rounded px-2 py-1 text-neutral-200">
            <option value="none">None</option>
            <option value="vendor">Vendor</option>
            <option value="sender">Sender</option>
            <option value="date">Month</option>
          </select>
        </label>
        <label className="flex items-center gap-1 text-neutral-400">
          Sort:
          <select value={sortBy} onChange={e => setSortBy(e.target.value as SortBy)}
                  className="bg-neutral-800 border border-neutral-700 rounded px-2 py-1 text-neutral-200">
            <option value="date">Date</option>
            <option value="amount">Amount</option>
            <option value="name">Name</option>
          </select>
        </label>
      </div>

      {/* Grouped document list */}
      <div className="space-y-4">
        {[...groups.entries()].map(([group, groupDocs]) => {
          const sorted = sortDocs(groupDocs, sortBy);
          const total = sorted.reduce((sum, d) => sum + (d.extractedAmount ?? 0), 0);
          return (
            <GroupSection
              key={group}
              title={group}
              count={sorted.length}
              total={total}
              docs={sorted}
              onSelectDocument={onSelectDocument}
              onContextMenu={handleContextMenu}
              selected={selected}
              onToggleSelect={toggleSelect}
              collapsed={groupBy !== 'none'}
            />
          );
        })}
      </div>

      {/* Context menu */}
      {contextMenu && (
        <div
          className="fixed bg-neutral-800 border border-neutral-700 rounded-lg shadow-xl py-1 z-50 min-w-48"
          style={{ left: contextMenu.x, top: contextMenu.y }}
        >
          <div className="px-3 py-1 text-[10px] text-neutral-500 uppercase tracking-wider">Move to</div>
          {categories?.filter(c => c.category !== category).map(c => (
            <button key={c.category} onClick={() => moveToCategory(c.category)}
                    className="w-full text-left px-3 py-1.5 text-sm hover:bg-neutral-700 transition-colors">
              📁 {c.category}
            </button>
          ))}
          <div className="border-t border-neutral-700 my-1" />
          <button onClick={() => { batchAction('star', ''); setContextMenu(null); }}
                  className="w-full text-left px-3 py-1.5 text-sm hover:bg-neutral-700">⭐ Star</button>
          <button onClick={() => { onSelectDocument(contextMenu.docId); setContextMenu(null); }}
                  className="w-full text-left px-3 py-1.5 text-sm hover:bg-neutral-700">📄 Open</button>
        </div>
      )}
    </div>
  );
}

function GroupSection({ title, count, total, docs, onSelectDocument, onContextMenu, selected, onToggleSelect, collapsed: defaultCollapsed }: {
  title: string; count: number; total: number;
  docs: DocumentSummary[]; onSelectDocument: (id: number) => void;
  onContextMenu: (e: React.MouseEvent, id: number) => void;
  selected: Set<number>; onToggleSelect: (id: number) => void;
  collapsed: boolean;
}) {
  const [collapsed, setCollapsed] = useState(defaultCollapsed && count > 5);

  return (
    <div>
      <button
        onClick={() => setCollapsed(!collapsed)}
        className="w-full text-left flex items-center gap-2 py-1 text-sm hover:text-neutral-100 transition-colors"
      >
        <span className="text-neutral-500 text-xs">{collapsed ? '▸' : '▾'}</span>
        <span className="font-medium">{title}</span>
        <span className="text-xs text-neutral-500">({count})</span>
        {total > 0 && <span className="ml-auto text-xs text-neutral-400">${total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</span>}
      </button>

      {!collapsed && (
        <div className="ml-4 space-y-0.5">
          {docs.map(doc => (
            <div
              key={doc.id}
              onContextMenu={e => onContextMenu(e, doc.id)}
              className={`flex items-center gap-2 px-2 py-1.5 rounded transition-colors text-sm ${
                selected.has(doc.id) ? 'bg-blue-950/40' : 'hover:bg-neutral-800/50'
              }`}
            >
              <input
                type="checkbox"
                checked={selected.has(doc.id)}
                onChange={() => onToggleSelect(doc.id)}
                className="accent-blue-500 shrink-0"
              />
              <button onClick={() => onSelectDocument(doc.id)} className="flex-1 text-left flex items-center gap-3 min-w-0">
                <span className="flex-1 truncate text-neutral-300">{doc.originalName}</span>
                {doc.extractedDate && <span className="text-xs text-neutral-600 shrink-0">{doc.extractedDate}</span>}
                {doc.extractedAmount != null && <span className="text-xs text-neutral-400 shrink-0">${doc.extractedAmount.toFixed(2)}</span>}
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
