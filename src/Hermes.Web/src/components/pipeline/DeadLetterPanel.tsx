import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { request } from '../../api/http';
import { useAccess } from '../../hooks/useAccess';
import { UnlockReason } from '../AccessPanel';

interface DeadLetter {
  id: number;
  docId: number;
  stage: string;
  error: string;
  originalName: string;
  failedAt: string;
}

async function fetchDeadLetters(): Promise<DeadLetter[]> {
  const res = await request('/api/dead-letters');
  return res.json();
}

export function DeadLetterPanel() {
  const access = useAccess();
  const [pending, setPending] = useState(false);
  const [feedback, setFeedback] = useState<{ error: boolean; message: string } | null>(null);
  const queryClient = useQueryClient();
  const { data: letters, error } = useQuery({ queryKey: ['dead-letters'], queryFn: fetchDeadLetters, refetchInterval: 30000 });

  if (error) return <p role="alert">{error.message}</p>;
  if (!letters || letters.length === 0) return null;

  const handleDismiss = async () => {
    if (pending || !access.can('mutate')) return;
    setPending(true);
    try {
      await request('/api/dead-letters/dismiss', { method: 'POST' });
      setFeedback({ error: false, message: 'Dead letters dismissed.' });
      queryClient.invalidateQueries({ queryKey: ['dead-letters'] });
    } catch (error) { setFeedback({ error: true, message: error instanceof Error ? error.message : 'Dismiss failed.' }); }
    finally { setPending(false); }
  };

  return (
    <div className="bg-red-950/30 border border-red-900/50 rounded-lg p-4">
      {!access.can('mutate') && <UnlockReason />}
      {feedback && <p role={feedback.error ? 'alert' : 'status'}>{feedback.message}</p>}
      <div className="flex items-center justify-between mb-3">
        <span className="text-sm font-semibold text-red-400">🔴 {letters.length} failed</span>
        <button onClick={handleDismiss} disabled={pending || !access.can('mutate')} className="text-xs text-neutral-500 hover:text-neutral-300">Dismiss all</button>
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
