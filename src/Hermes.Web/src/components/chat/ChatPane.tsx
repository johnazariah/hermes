import { useState, useRef, useEffect, useCallback } from 'react';
import { chat, sourceTitle, type ChatSource } from '../../api/chat';
import { useAccess } from '../../hooks/useAccess';
import { UnlockReason } from '../AccessPanel';

interface ChatMessage {
  role: 'user' | 'hermes';
  text: string;
  results?: ChatSource[];
  aiAnswer?: string;
  error?: boolean;
}

export function SourceCard({ source, onSelectDocument }: {
  source: ChatSource;
  onSelectDocument: (id: number) => void;
}) {
  const documentBacked = source.savedPath.trim().length > 0;
  const content = <>
    <span className="text-neutral-500 text-xs">{source.category}</span>
    <span className="flex-1 min-w-0">
      <span className="block truncate">
        {sourceTitle(source)}
      </span>
      {!documentBacked && <span className="block text-xs text-neutral-500">
        {source.resultType === 'email' ? 'Email metadata only — no archived document' : 'Document file unavailable'}
      </span>}
      {source.sender && <span className="block text-xs text-neutral-500">{source.sender}</span>}
      {source.snippet && <span className="block text-xs text-neutral-400">{source.snippet}</span>}
    </span>
    {source.extractedAmount != null && (
      <span className="text-neutral-400 text-xs">${source.extractedAmount.toFixed(2)}</span>
    )}
  </>;
  const className = 'w-full text-left px-3 py-2 rounded bg-neutral-900/50 flex items-center gap-3 text-sm';
  return documentBacked
    ? <button onClick={() => onSelectDocument(source.documentId)}
        className={`${className} hover:bg-neutral-800 transition-colors`}>{content}</button>
    : <div className={className}>{content}</div>;
}

export function ChatPane({ onSelectDocument }: { onSelectDocument: (id: number) => void }) {
  const access = useAccess();
  const [aiEnabled, setAiEnabled] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const requestRef = useRef<AbortController | null>(null);

  useEffect(() => () => requestRef.current?.abort(), []);

  useEffect(() => {
    scrollRef.current?.scrollTo(0, scrollRef.current.scrollHeight);
  }, [messages]);

  const handleSend = useCallback(async () => {
    const query = input.trim();
    if (!query || loading || requestRef.current) return;

    const controller = new AbortController();
    requestRef.current = controller;
    setInput('');
    setMessages(prev => [...prev, { role: 'user', text: query }, { role: 'hermes', text: 'Searching...' }]);
    setLoading(true);

    try {
      await chat(query, event => {
        if (controller.signal.aborted || event.type === 'done') return;
        setMessages(previous => previous.map((message, index) => {
          if (index !== previous.length - 1) return message;
          return event.type === 'results'
            ? { ...message, text: `Found ${event.results.length} source(s)`, results: event.results }
            : { ...message, aiAnswer: event.answer };
        }));
      }, controller.signal, aiEnabled);
    } catch (error) {
      if (!controller.signal.aborted) {
        setMessages(previous => previous.map((message, index) =>
          index === previous.length - 1
            ? { ...message, error: true, text: error instanceof Error ? error.message : 'Chat request failed.' }
            : message,
        ));
      }
    } finally {
      requestRef.current = null;
      if (!controller.signal.aborted) setLoading(false);
    }
  }, [input, loading, aiEnabled]);

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
              <div className="space-y-3" role={msg.error ? 'alert' : undefined}>
                {msg.error && <div className="text-red-500">{msg.text}</div>}
                {/* AI answer */}
                {msg.aiAnswer && (
                  <div className="bg-neutral-900 border border-neutral-800 rounded-lg px-4 py-3 text-sm leading-relaxed">
                    {msg.aiAnswer}
                  </div>
                )}

                {/* Document cards */}
                {msg.results && msg.results.length > 0 && (
                  <ul className="space-y-1">
                    {msg.results.slice(0, 10).map(r => (
                      <li key={`${r.resultType}:${r.documentId}:${r.savedPath}`}>
                        <SourceCard source={r} onSelectDocument={onSelectDocument} />
                      </li>
                    ))}
                  </ul>
                )}

                {/* Fallback text if no results and no answer */}
                {!msg.error && !msg.aiAnswer && (!msg.results || msg.results.length === 0) && (
                  <div className="text-sm text-neutral-500">{msg.text}</div>
                )}
              </div>
            )}
          </div>
        ))}
      </div>

      {/* Input */}
      <div className="border-t border-neutral-800 pt-3">
        <label className="block text-sm mb-2">
          <input type="checkbox" checked={aiEnabled} disabled={loading || !access.can('model.invoke')}
            onChange={event => setAiEnabled(event.target.checked)} /> Enable AI answer
        </label>
        {!access.can('model.invoke') && <UnlockReason capability="model.invoke" />}
        {!aiEnabled && <p className="text-xs mb-2">Search-only mode: no model calls.</p>}
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
