import { useQuery } from '@tanstack/react-query';
import { fetchDocuments } from '../../api/hermes';
import type { DocumentSummary } from '../../types/hermes';

export function DocumentList({ category, onSelectDocument }: {
  category: string;
  onSelectDocument: (id: number) => void;
}) {
  const { data: docs, isLoading } = useQuery<DocumentSummary[]>({
    queryKey: ['documents', category],
    queryFn: () => fetchDocuments(category, 0, 100),
  });

  if (isLoading) return <div className="text-neutral-500 text-sm">Loading…</div>;

  return (
    <div>
      <h2 className="text-lg font-semibold mb-4">📁 {category}</h2>
      {docs && docs.length > 0 ? (
        <div className="space-y-1">
          {docs.map(doc => (
            <button
              key={doc.id}
              onClick={() => onSelectDocument(doc.id)}
              className="w-full text-left px-3 py-2 rounded hover:bg-neutral-800/50 flex items-center gap-3 transition-colors"
            >
              <div className="flex-1 min-w-0">
                <div className="text-sm truncate">{doc.originalName}</div>
                <div className="text-xs text-neutral-500 truncate">
                  {[doc.sender, doc.extractedDate, doc.extractedAmount != null ? `$${doc.extractedAmount.toFixed(2)}` : null]
                    .filter(Boolean).join(' · ') || 'No metadata'}
                </div>
              </div>
              {doc.classificationTier && (
                <span className="text-[10px] text-neutral-600 shrink-0">{doc.classificationTier}</span>
              )}
            </button>
          ))}
        </div>
      ) : (
        <div className="text-sm text-neutral-500">No documents in this category.</div>
      )}
    </div>
  );
}
