import { useState } from 'react';

export function BatchBar({
  selectedCount,
  onTag,
  onReclassify,
  categories = [],
  reclassificationPending = false,
  onClearSelection,
}: {
  selectedCount: number;
  onTag: (tag: string) => void;
  onReclassify?: (category: string) => void;
  categories?: string[];
  reclassificationPending?: boolean;
  onClearSelection: () => void;
}) {
  const [tagInput, setTagInput] = useState('');
  const [categoryInput, setCategoryInput] = useState('');

  if (selectedCount === 0) return null;

  const handleTag = () => {
    const trimmed = tagInput.trim();
    if (!trimmed) return;
    onTag(trimmed);
    setTagInput('');
  };

  const handleReclassify = () => {
    const category = categoryInput.trim();
    if (!category || reclassificationPending || !onReclassify) return;
    onReclassify(category);
  };

  return (
    <div
      className="flex flex-wrap items-center gap-3 px-4 py-2.5 bg-blue-50 dark:bg-blue-950/50 border-t border-blue-200 dark:border-blue-800"
      aria-busy={reclassificationPending}
    >
      <span className="text-sm font-medium text-blue-900 dark:text-blue-200">
        {selectedCount} selected
      </span>

      <div className="flex items-center gap-1.5">
        <span className="text-xs text-blue-700 dark:text-blue-400">Tag as:</span>
        <input
          type="text"
          value={tagInput}
          onChange={(e) => setTagInput(e.target.value)}
          disabled={reclassificationPending}
          onKeyDown={(e) => {
            if (e.key === 'Enter') handleTag();
          }}
          placeholder="tag name…"
          className="px-2 py-1 text-xs rounded border border-blue-300 dark:border-blue-700 bg-white dark:bg-neutral-900 text-neutral-900 dark:text-neutral-200 placeholder-neutral-400 focus:outline-none focus:ring-1 focus:ring-blue-500 w-32"
        />
        <button
          onClick={handleTag}
          disabled={!tagInput.trim() || reclassificationPending}
          className="px-2.5 py-1 text-xs font-medium rounded bg-blue-600 text-white hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
        >
          Apply
        </button>
      </div>

      {onReclassify && (
        <div className="flex items-center gap-1.5">
          <label htmlFor="batch-category" className="text-xs text-blue-700 dark:text-blue-400">
            Category:
          </label>
          <input
            id="batch-category"
            type="text"
            list="batch-category-options"
            value={categoryInput}
            onChange={(e) => setCategoryInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') handleReclassify();
            }}
            disabled={reclassificationPending}
            required
            placeholder="Choose or enter…"
            className="px-2 py-1 text-xs rounded border border-blue-300 dark:border-blue-700 bg-white dark:bg-neutral-900 text-neutral-900 dark:text-neutral-200 placeholder-neutral-400 focus:outline-none focus:ring-1 focus:ring-blue-500 w-36"
          />
          <datalist id="batch-category-options">
            {categories.map((category) => (
              <option key={category} value={category} />
            ))}
          </datalist>
          <button
            type="button"
            onClick={handleReclassify}
            disabled={!categoryInput.trim() || reclassificationPending}
            className="px-2.5 py-1 text-xs font-medium rounded bg-amber-600 text-white hover:bg-amber-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
          >
            {reclassificationPending ? 'Reclassifying…' : 'Reclassify'}
          </button>
        </div>
      )}

      <div className="ml-auto flex items-center gap-2">
        <button
          onClick={onClearSelection}
          disabled={reclassificationPending}
          className="text-xs text-neutral-500 hover:text-neutral-700 dark:hover:text-neutral-300 transition-colors"
        >
          Clear
        </button>
      </div>
    </div>
  );
}
