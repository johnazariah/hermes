import { useState } from 'react';
import { Sidebar } from './Sidebar';
import { ChatPane } from '../chat/ChatPane';

export function Shell({ selectedCategory, onSelectCategory, onSelectDocument, onOpenSettings, children }: {
  selectedCategory: string | null;
  onSelectCategory: (category: string | null) => void;
  onSelectDocument: (id: number) => void;
  onOpenSettings: () => void;
  children: React.ReactNode;
}) {
  const [chatOpen, setChatOpen] = useState(false);

  return (
    <div className="flex h-screen bg-neutral-950 text-neutral-200">
      <Sidebar selectedCategory={selectedCategory} onSelectCategory={onSelectCategory} onOpenSettings={onOpenSettings} />
      <main className="flex-1 overflow-y-auto p-6">
        {children}
      </main>
      {/* Chat toggle + panel */}
      <button
        onClick={() => setChatOpen(!chatOpen)}
        className="fixed bottom-4 right-4 w-12 h-12 bg-blue-600 rounded-full flex items-center justify-center text-white shadow-lg hover:bg-blue-500 z-50"
        title="Chat"
      >
        💬
      </button>
      {chatOpen && (
        <aside className="w-96 bg-neutral-900 border-l border-neutral-800 flex flex-col p-4">
          <div className="flex items-center justify-between mb-3">
            <span className="text-sm font-semibold">Chat</span>
            <button onClick={() => setChatOpen(false)} className="text-neutral-500 hover:text-neutral-300 text-xs">✕</button>
          </div>
          <ChatPane onSelectDocument={onSelectDocument} />
        </aside>
      )}
    </div>
  );
}
