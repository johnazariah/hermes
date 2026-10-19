import { useNavigate } from 'react-router-dom';
import { ChatPane } from '../components/chat/ChatPane';

export function ChatPage() {
  const navigate = useNavigate();
  return (
    <section className="max-w-3xl mx-auto flex flex-col h-[calc(100vh-7rem)]">
      <h1 className="text-xl font-bold mb-2">Chat</h1>
      <p className="text-sm text-neutral-500 mb-4">Ask questions about your documents</p>
      <ChatPane onSelectDocument={id => navigate(`/documents?id=${id}`)} />
    </section>
  );
}
