import { useState } from 'react';

export function BatchBar({
  selectedCount,
  onTag,
  onClearSelection,
}: {
  selectedCount: number;
  onTag: (tag: string) => void;
  onClearSelection: () => void;
}) {
  const [tagInput, setTagInput] = useState('');

  if (selectedCount === 0) return null;

  const handleTag = () => {
    const trimmed = tagInput.trim();
    if (!trimmed) return;
    onTag(trimmed);
    setTagInput('');
  };

  return (
    <div className="flex items-center gap-3 px-4 py-2.5 bg-blue-50 dark:bg-blue-950/50 border-t border-blue-200 dark:border-blue-800">
      <span className="text-sm font-medium text-blue-900 dark:text-blue-200">
        {selectedCount} selected
      </span>

      <div className="flex items-center gap-1.5">
        <span className="text-xs text-blue-700 dark:text-blue-400">Tag as:</span>
        <input
          type="text"
          value={tagInput}
          onChange={(e) => setTagInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') handleTag();
          }}
          placeholder="tag name…"
          className="px-2 py-1 text-xs rounded border border-blue-300 dark:border-blue-700 bg-white dark:bg-neutral-900 text-neutral-900 dark:text-neutral-200 placeholder-neutral-400 focus:outline-none focus:ring-1 focus:ring-blue-500 w-32"
        />
        <button
          onClick={handleTag}
          disabled={!tagInput.trim()}
          className="px-2.5 py-1 text-xs font-medium rounded bg-blue-600 text-white hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
        >
          Apply
        </button>
      </div>

      <div className="ml-auto flex items-center gap-2">
        <button
          className="px-2.5 py-1 text-xs rounded border border-neutral-300 dark:border-neutral-600 text-neutral-600 dark:text-neutral-400 opacity-50 cursor-not-allowed"
          title="Coming soon"
          disabled
        >
          Export
        </button>
        <button
          className="px-2.5 py-1 text-xs rounded border border-red-300 dark:border-red-800 text-red-600 dark:text-red-400 opacity-50 cursor-not-allowed"
          title="Coming soon"
          disabled
        >
          Delete
        </button>
        <button
          onClick={onClearSelection}
          className="text-xs text-neutral-500 hover:text-neutral-700 dark:hover:text-neutral-300 transition-colors"
        >
          Clear
        </button>
      </div>
    </div>
  );
}
