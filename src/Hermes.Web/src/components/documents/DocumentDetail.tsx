import { useQuery } from '@tanstack/react-query';
import { fetchDocumentDetail, fetchDocumentContent } from '../../api/hermes';
import type { DocumentDetail as DocumentDetailType } from '../../types/hermes';

export function DocumentDetail({ documentId, onBack }: {
  documentId: number;
  onBack: () => void;
}) {
  const { data: detail } = useQuery<DocumentDetailType>({
    queryKey: ['document', documentId],
    queryFn: () => fetchDocumentDetail(documentId),
  });
  const { data: content } = useQuery<{ markdown: string }>({
    queryKey: ['document-content', documentId],
    queryFn: () => fetchDocumentContent(documentId),
    enabled: detail?.pipelineStatus.extracted === true,
  });

  if (!detail) return <div className="text-neutral-500 text-sm">Loading…</div>;

  const doc = detail.summary;

  return (
    <div className="max-w-3xl">
      <button onClick={onBack} className="text-xs text-neutral-500 hover:text-neutral-300 mb-4">← Back</button>

      <h2 className="text-lg font-semibold mb-1">{doc.originalName}</h2>

      {/* Metadata grid */}
      <div className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm mb-6">
        {doc.sender && <Field label="Sender" value={doc.sender} />}
        {doc.extractedDate && <Field label="Date" value={doc.extractedDate} />}
        {doc.extractedAmount != null && <Field label="Amount" value={`$${doc.extractedAmount.toFixed(2)}`} />}
        {detail.vendor && <Field label="Vendor" value={detail.vendor} />}
        <Field label="Category" value={doc.category} />
        <Field label="Ingested" value={new Date(detail.ingestedAt).toLocaleDateString()} />
        {detail.extractedAt && <Field label="Extracted" value={new Date(detail.extractedAt).toLocaleDateString()} />}
        {doc.classificationTier && <Field label="Tier" value={doc.classificationTier} />}
      </div>

      {/* Pipeline status */}
      <div className="flex gap-2 mb-6">
        <StatusBadge label="Classified" done={detail.pipelineStatus.classified} />
        <StatusBadge label="Extracted" done={detail.pipelineStatus.extracted} />
        <StatusBadge label="Embedded" done={detail.pipelineStatus.embedded} />
      </div>

      {/* Content */}
      {content?.markdown ? (
        <div className="bg-neutral-900 border border-neutral-800 rounded-lg p-4 text-sm whitespace-pre-wrap font-mono leading-relaxed max-h-[60vh] overflow-y-auto">
          {content.markdown}
        </div>
      ) : detail.pipelineStatus.extracted ? (
        <div className="text-sm text-neutral-500">No content available.</div>
      ) : (
        <div className="text-sm text-neutral-500">Awaiting extraction…</div>
      )}
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <>
      <span className="text-neutral-500">{label}</span>
      <span className="text-neutral-300 truncate">{value}</span>
    </>
  );
}

function StatusBadge({ label, done }: { label: string; done: boolean }) {
  return (
    <span className={`text-xs px-2 py-0.5 rounded ${done ? 'bg-green-900/50 text-green-400' : 'bg-neutral-800 text-neutral-500'}`}>
      {done ? '✓' : '○'} {label}
    </span>
  );
}
