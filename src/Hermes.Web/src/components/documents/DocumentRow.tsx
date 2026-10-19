import type { DocumentSummary } from '../../types/hermes';

const CATEGORY_COLORS = [
  'bg-blue-100 text-blue-800 dark:bg-blue-900/50 dark:text-blue-300',
  'bg-amber-100 text-amber-800 dark:bg-amber-900/50 dark:text-amber-300',
  'bg-green-100 text-green-800 dark:bg-green-900/50 dark:text-green-300',
  'bg-purple-100 text-purple-800 dark:bg-purple-900/50 dark:text-purple-300',
  'bg-pink-100 text-pink-800 dark:bg-pink-900/50 dark:text-pink-300',
  'bg-teal-100 text-teal-800 dark:bg-teal-900/50 dark:text-teal-300',
];

function categoryColor(category: string): string {
  let hash = 0;
  for (let i = 0; i < category.length; i++) {
    hash = ((hash << 5) - hash + category.charCodeAt(i)) | 0;
  }
  return CATEGORY_COLORS[Math.abs(hash) % CATEGORY_COLORS.length];
}

function confidenceColor(confidence: number | null): string {
  if (confidence == null) return 'bg-neutral-400';
  if (confidence >= 0.8) return 'bg-green-500';
  if (confidence >= 0.5) return 'bg-yellow-500';
  return 'bg-red-500';
}

function formatDate(date: string | null): string {
  if (!date) return '';
  try {
    return new Date(date).toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
    });
  } catch {
    return date;
  }
}

export function DocumentRow({
  doc,
  selected,
  onToggle,
  disabled = false,
}: {
  doc: DocumentSummary;
  selected: boolean;
  onToggle: (id: number) => void;
  disabled?: boolean;
}) {
  return (
    <div
      className={`flex items-center gap-3 px-4 py-2.5 border-b border-neutral-100 dark:border-neutral-800/50 hover:bg-neutral-50 dark:hover:bg-neutral-800/30 transition-colors ${
        selected ? 'bg-blue-50/50 dark:bg-blue-900/10' : ''
      }`}
    >
      <input
        type="checkbox"
        checked={selected}
        onChange={() => onToggle(doc.id)}
        disabled={disabled}
        aria-label={`Select ${doc.originalName}`}
        className="h-4 w-4 rounded border-neutral-300 dark:border-neutral-600 accent-blue-600 cursor-pointer shrink-0"
      />

      <div className="flex-1 min-w-0">
        <div className="text-sm text-neutral-900 dark:text-neutral-200 truncate">
          {doc.originalName}
        </div>
        <div className="flex items-center gap-2 mt-0.5 text-xs text-neutral-500">
          {doc.sender && <span className="truncate max-w-40">{doc.sender}</span>}
          {doc.extractedDate && <span>{formatDate(doc.extractedDate)}</span>}
        </div>
      </div>

      <span
        className={`text-[10px] font-medium px-2 py-0.5 rounded-full shrink-0 ${categoryColor(doc.category)}`}
      >
        {doc.category}
      </span>

      {doc.extractedAmount != null && (
        <span className="text-sm font-mono text-neutral-700 dark:text-neutral-300 tabular-nums w-24 text-right shrink-0">
          ${doc.extractedAmount.toFixed(2)}
        </span>
      )}

      <span
        title={
          doc.classificationConfidence != null
            ? `${(doc.classificationConfidence * 100).toFixed(0)}% confidence`
            : 'Unknown'
        }
        className={`h-2 w-2 rounded-full shrink-0 ${confidenceColor(doc.classificationConfidence)}`}
      />
    </div>
  );
}
