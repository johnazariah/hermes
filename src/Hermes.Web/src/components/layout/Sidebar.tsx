import { usePipelineState } from '../../hooks/usePipelineState';
import type { PipelineState } from '../../types/hermes';

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

function StatusDot({ state }: { state: PipelineState }) {
  const active = state.extractQueueDepth > 0 || state.ingestQueueDepth > 0;
  return (
    <span className={`inline-block w-2 h-2 rounded-full ${active ? 'bg-green-400 animate-pulse' : 'bg-neutral-500'}`} />
  );
}

export function Sidebar() {
  const state = usePipelineState();

  return (
    <aside className="w-64 bg-neutral-900 border-r border-neutral-800 flex flex-col h-full overflow-y-auto">
      {/* Header */}
      <div className="px-4 py-3 border-b border-neutral-800 flex items-center gap-2">
        <span className="text-lg">⚡</span>
        <span className="text-sm font-bold tracking-widest text-neutral-200">HERMES</span>
        <span className="ml-auto"><StatusDot state={state} /></span>
      </div>

      {/* Pipeline stages */}
      <div className="px-4 py-3 space-y-4">
        <StageRow label="Extracted" value={state.totalExtracted} total={state.totalDocuments} />
        <StageRow label="Embedded" value={state.totalEmbedded} total={state.totalDocuments} />

        {state.extractQueueDepth > 0 && (
          <div className="text-xs text-blue-400">
            ⏳ Extracting... {state.currentDoc && <span className="text-neutral-500">({state.currentDoc})</span>}
          </div>
        )}

        {state.deadLetterCount > 0 && (
          <div className="text-xs text-red-400">
            🔴 {state.deadLetterCount} failed
          </div>
        )}
      </div>

      {/* Stats */}
      <div className="px-4 py-2 border-t border-neutral-800 mt-auto">
        <div className="text-xs text-neutral-500">
          {state.totalDocuments.toLocaleString()} documents
        </div>
      </div>
    </aside>
  );
}
