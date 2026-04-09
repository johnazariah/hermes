import { useQuery } from '@tanstack/react-query';
import { fetchStats, fetchCategories, triggerSync } from '../../api/hermes';
import type { IndexStats, CategoryCount } from '../../types/hermes';

function StageRow({ label, value, total }: { label: string; value: number; total: number }) {
  const pct = total > 0 ? (value / total) * 100 : 0;
  return (
    <div className="space-y-1">
      <div className="flex justify-between text-xs text-neutral-400">
        <span>{label}</span>
        <span>{value.toLocaleString()} / {total.toLocaleString()}</span>
      </div>
      <div className="h-1.5 bg-neutral-700 rounded-full overflow-hidden">
        <div className="h-full bg-blue-500 rounded-full transition-all" style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

export function Sidebar({ onSelectCategory, selectedCategory }: {
  onSelectCategory: (category: string | null) => void;
  selectedCategory: string | null;
}) {
  const { data: stats } = useQuery<IndexStats>({ queryKey: ['stats'], queryFn: fetchStats, refetchInterval: 5000 });
  const { data: categories } = useQuery<CategoryCount[]>({ queryKey: ['categories'], queryFn: fetchCategories, refetchInterval: 10000 });

  const total = stats?.documentCount ?? 0;
  const extracted = stats?.extractedCount ?? 0;
  const embedded = stats?.embeddedCount ?? 0;
  const extracting = total - extracted;

  return (
    <aside className="w-64 bg-neutral-900 border-r border-neutral-800 flex flex-col h-full overflow-y-auto">
      {/* Header */}
      <div className="px-4 py-3 border-b border-neutral-800 flex items-center gap-2">
        <span className="text-lg">⚡</span>
        <span className="text-sm font-bold tracking-widest text-neutral-200">HERMES</span>
        <button onClick={() => triggerSync()} className="ml-auto text-neutral-500 hover:text-neutral-200 text-sm" title="Sync Now">⟳</button>
      </div>

      {/* Pipeline progress */}
      <div className="px-4 py-3 space-y-3 border-b border-neutral-800">
        <div className="text-[10px] font-semibold tracking-widest text-neutral-500">PIPELINE</div>
        <StageRow label="🔍 Extracted" value={extracted} total={total} />
        <StageRow label="🧠 Embedded" value={embedded} total={total} />
        {extracting > 0 && (
          <div className="text-xs text-blue-400">⏳ {extracting.toLocaleString()} awaiting extraction</div>
        )}
        {stats && (
          <div className="text-[10px] text-neutral-600">DB: {stats.databaseSizeMb.toFixed(1)} MB</div>
        )}
      </div>

      {/* Library */}
      <div className="px-4 py-3 flex-1">
        <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
          LIBRARY {total > 0 && <span className="text-neutral-600">({total.toLocaleString()})</span>}
        </div>
        {categories && categories.length > 0 ? (
          <div className="space-y-0.5">
            {categories.map(cat => (
              <button
                key={cat.category}
                onClick={() => onSelectCategory(cat.category)}
                className={`w-full text-left px-2 py-1.5 rounded text-sm flex justify-between items-center transition-colors ${
                  selectedCategory === cat.category
                    ? 'bg-neutral-800 text-neutral-100'
                    : 'hover:bg-neutral-800/50 text-neutral-400'
                }`}
              >
                <span>{cat.category}</span>
                <span className="text-xs text-neutral-600">({cat.count})</span>
              </button>
            ))}
          </div>
        ) : (
          <div className="text-xs text-neutral-600">No documents yet.</div>
        )}
      </div>

      {/* Footer */}
      <div className="px-4 py-2 border-t border-neutral-800">
        <button
          onClick={() => onSelectCategory(null)}
          className="text-xs text-neutral-500 hover:text-neutral-300"
        >
          ← Home
        </button>
      </div>
    </aside>
  );
}
