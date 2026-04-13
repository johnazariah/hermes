import { useQuery, useQueryClient } from '@tanstack/react-query';

interface DeadLetter {
  id: number;
  docId: number;
  stage: string;
  error: string;
  originalName: string;
  failedAt: string;
}

async function fetchDeadLetters(): Promise<DeadLetter[]> {
  const res = await fetch('/api/dead-letters');
  return res.json();
}

export function DeadLetterPanel() {
  const queryClient = useQueryClient();
  const { data: letters } = useQuery({ queryKey: ['dead-letters'], queryFn: fetchDeadLetters, refetchInterval: 30000 });

  if (!letters || letters.length === 0) return null;

  const handleDismiss = async () => {
    await fetch('/api/dead-letters/dismiss', { method: 'POST' });
    queryClient.invalidateQueries({ queryKey: ['dead-letters'] });
  };

  return (
    <div className="bg-red-950/30 border border-red-900/50 rounded-lg p-4">
      <div className="flex items-center justify-between mb-3">
        <span className="text-sm font-semibold text-red-400">🔴 {letters.length} failed</span>
        <button onClick={handleDismiss} className="text-xs text-neutral-500 hover:text-neutral-300">Dismiss all</button>
      </div>
      <div className="space-y-2 max-h-60 overflow-y-auto">
        {letters.map(l => (
          <div key={l.id} className="text-xs">
            <div className="text-neutral-300 truncate">{l.originalName}</div>
            <div className="text-neutral-500">{l.error}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
