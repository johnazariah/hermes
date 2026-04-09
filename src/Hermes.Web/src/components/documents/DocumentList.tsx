import { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchDocuments } from '../../api/hermes';
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
  const [groupBy, setGroupBy] = useState<GroupBy>('vendor');
  const [sortBy, setSortBy] = useState<SortBy>('date');
  const { data: docs, isLoading } = useQuery<DocumentSummary[]>({
    queryKey: ['documents', category],
    queryFn: () => fetchDocuments(category, 0, 500),
  });

  const groups = useMemo(() => {
    if (!docs) return new Map();
    return groupDocs(docs, groupBy);
  }, [docs, groupBy]);

  if (isLoading) return <div className="text-neutral-500 text-sm">Loading…</div>;

  return (
    <div>
      <div className="flex items-center gap-4 mb-4">
        <h2 className="text-lg font-semibold">📁 {category}</h2>
        <span className="text-xs text-neutral-500">({docs?.length ?? 0})</span>
      </div>

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
              collapsed={groupBy !== 'none'}
            />
          );
        })}
      </div>
    </div>
  );
}

function GroupSection({ title, count, total, docs, onSelectDocument, collapsed: defaultCollapsed }: {
  title: string; count: number; total: number;
  docs: DocumentSummary[]; onSelectDocument: (id: number) => void;
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
            <button
              key={doc.id}
              onClick={() => onSelectDocument(doc.id)}
              className="w-full text-left px-3 py-1.5 rounded hover:bg-neutral-800/50 flex items-center gap-3 transition-colors text-sm"
            >
              <span className="flex-1 truncate text-neutral-300">{doc.originalName}</span>
              {doc.extractedDate && <span className="text-xs text-neutral-600 shrink-0">{doc.extractedDate}</span>}
              {doc.extractedAmount != null && <span className="text-xs text-neutral-400 shrink-0">${doc.extractedAmount.toFixed(2)}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
