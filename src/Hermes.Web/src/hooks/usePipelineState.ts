import { useState, useEffect } from 'react';
import type { PipelineState } from '../types/hermes';

const initial: PipelineState = {
  ingestQueueDepth: 0,
  extractQueueDepth: 0,
  deadLetterCount: 0,
  totalDocuments: 0,
  totalExtracted: 0,
  totalEmbedded: 0,
  currentDoc: null,
  lastUpdated: new Date().toISOString(),
};

export function usePipelineState(): PipelineState {
  const [state, setState] = useState<PipelineState>(initial);

  useEffect(() => {
    const source = new EventSource('/api/pipeline/state');
    source.onmessage = (e) => {
      try {
        setState(JSON.parse(e.data));
      } catch { /* ignore parse errors */ }
    };
    source.onerror = () => {
      source.close();
      // Reconnect after 5s
      setTimeout(() => setState(prev => ({ ...prev })), 5000);
    };
    return () => source.close();
  }, []);

  return state;
}
