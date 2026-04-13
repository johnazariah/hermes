import { useState, useRef, useEffect, useCallback } from 'react';

interface SearchResult {
  documentId: number;
  originalName: string;
  category: string;
  sender: string | null;
  extractedAmount: number | null;
  snippet: string | null;
}

interface ChatMessage {
  role: 'user' | 'hermes';
  text: string;
  results?: SearchResult[];
  aiAnswer?: string;
}

export function ChatPane({ onSelectDocument }: { onSelectDocument: (id: number) => void }) {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo(0, scrollRef.current.scrollHeight);
  }, [messages]);

  const handleSend = useCallback(async () => {
    const query = input.trim();
    if (!query || loading) return;

    setInput('');
    setMessages(prev => [...prev, { role: 'user', text: query }]);
    setLoading(true);

    try {
      const response = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ query, aiEnabled: true }),
      });

      const reader = response.body?.getReader();
      if (!reader) return;

      const decoder = new TextDecoder();
      let buffer = '';
      let results: SearchResult[] = [];
      let aiAnswer = '';

      // Add placeholder Hermes message
      setMessages(prev => [...prev, { role: 'hermes', text: 'Searching...' }]);

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        // Parse SSE events from buffer
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        let eventType = '';
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            eventType = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            const data = line.slice(6);
            try {
              const parsed = JSON.parse(data);
              if (eventType === 'results' && parsed.results) {
                results = parsed.results;
                setMessages(prev => {
                  const updated = [...prev];
                  const last = updated[updated.length - 1];
                  if (last.role === 'hermes') {
                    updated[updated.length - 1] = { ...last, text: `Found ${results.length} document(s)`, results };
                  }
                  return updated;
                });
              } else if (eventType === 'answer' && parsed.answer) {
                aiAnswer = parsed.answer;
                setMessages(prev => {
                  const updated = [...prev];
                  const last = updated[updated.length - 1];
                  if (last.role === 'hermes') {
                    updated[updated.length - 1] = { ...last, aiAnswer };
                  }
                  return updated;
                });
              }
            } catch { /* skip unparseable */ }
          }
        }
      }
    } catch {
      setMessages(prev => [...prev, { role: 'hermes', text: 'Error connecting to service.' }]);
    } finally {
      setLoading(false);
    }
  }, [input, loading]);

  return (
    <div className="flex flex-col h-full">
      {/* Messages */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto space-y-4 pb-4">
        {messages.length === 0 && (
          <div className="text-neutral-500 text-sm mt-8 text-center">
            <p className="mb-4">Ask Hermes anything about your documents.</p>
            <div className="flex flex-wrap gap-2 justify-center">
              {['find my land tax assessments', 'Costco statements 2024', 'how much did I spend on insurance?'].map(q => (
                <button
                  key={q}
                  onClick={() => { setInput(q); }}
                  className="text-xs px-3 py-1.5 bg-neutral-800 rounded-full hover:bg-neutral-700 transition-colors"
                >
                  {q}
                </button>
              ))}
            </div>
          </div>
        )}

        {messages.map((msg, i) => (
          <div key={i} className={`${msg.role === 'user' ? 'flex justify-end' : ''}`}>
            {msg.role === 'user' ? (
              <div className="bg-blue-900/30 text-blue-200 rounded-lg px-4 py-2 max-w-md text-sm">
                {msg.text}
              </div>
            ) : (
              <div className="space-y-3">
                {/* AI answer */}
                {msg.aiAnswer && (
                  <div className="bg-neutral-900 border border-neutral-800 rounded-lg px-4 py-3 text-sm leading-relaxed">
                    {msg.aiAnswer}
                  </div>
                )}

                {/* Document cards */}
                {msg.results && msg.results.length > 0 && (
                  <div className="space-y-1">
                    {msg.results.slice(0, 10).map(r => (
                      <button
                        key={r.documentId}
                        onClick={() => onSelectDocument(r.documentId)}
                        className="w-full text-left px-3 py-2 rounded bg-neutral-900/50 hover:bg-neutral-800 flex items-center gap-3 text-sm transition-colors"
                      >
                        <span className="text-neutral-500 text-xs">{r.category}</span>
                        <span className="flex-1 truncate">{r.originalName}</span>
                        {r.extractedAmount != null && (
                          <span className="text-neutral-400 text-xs">${r.extractedAmount.toFixed(2)}</span>
                        )}
                      </button>
                    ))}
                  </div>
                )}

                {/* Fallback text if no results and no answer */}
                {!msg.aiAnswer && (!msg.results || msg.results.length === 0) && (
                  <div className="text-sm text-neutral-500">{msg.text}</div>
                )}
              </div>
            )}
          </div>
        ))}
      </div>

      {/* Input */}
      <div className="border-t border-neutral-800 pt-3">
        <div className="flex gap-2">
          <input
            type="text"
            value={input}
            onChange={e => setInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') handleSend(); }}
            placeholder="Ask about your documents..."
            className="flex-1 bg-neutral-900 border border-neutral-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-blue-500"
            disabled={loading}
          />
          <button
            onClick={handleSend}
            disabled={loading || !input.trim()}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-500 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {loading ? '...' : 'Send'}
          </button>
        </div>
      </div>
    </div>
  );
}
