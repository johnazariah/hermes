import { useQuery } from '@tanstack/react-query';

interface ActivityEntry {
  id: number;
  timestamp: string;
  level: string;
  category: string;
  message: string;
  documentId: number | null;
}

const levelColors: Record<string, string> = {
  info: 'text-blue-400',
  warn: 'text-yellow-400',
  error: 'text-red-400',
};

const categoryIcons: Record<string, string> = {
  sync: '🔄',
  classify: '🏷',
  extract: '📄',
  embed: '🧠',
  reminder: '⏰',
};

export function ActivityPanel() {
  const { data: entries } = useQuery<ActivityEntry[]>({
    queryKey: ['activity'],
    queryFn: () => fetch('/api/activity?limit=100').then(r => r.json()),
    refetchInterval: 5000,
  });

  return (
    <div className="max-w-3xl">
      <h2 className="text-lg font-semibold mb-4">📋 Activity Log</h2>

      {!entries || entries.length === 0 ? (
        <div className="text-neutral-500 text-sm">No activity yet. The pipeline will log events as it processes documents.</div>
      ) : (
        <div className="space-y-1">
          {entries.map(e => (
            <div key={e.id} className="flex items-start gap-3 px-3 py-2 rounded hover:bg-neutral-900/50 text-sm">
              <span className="text-xs shrink-0 mt-0.5">{categoryIcons[e.category] ?? '•'}</span>
              <div className="flex-1 min-w-0">
                <span className={levelColors[e.level] ?? 'text-neutral-400'}>{e.message}</span>
              </div>
              <span className="text-[10px] text-neutral-600 shrink-0 mt-0.5">
                {formatTime(e.timestamp)}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function formatTime(ts: string): string {
  try {
    const d = new Date(ts);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch {
    return ts;
  }
}
