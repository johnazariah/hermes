import { useState, useRef, useEffect } from "react";

interface Message {
    role: "user" | "assistant";
    content: string;
    sources?: { id: number; name: string; category: string }[];
}

export function ChatPage() {
    const [messages, setMessages] = useState<Message[]>([]);
    const [input, setInput] = useState("");
    const [streaming, setStreaming] = useState(false);
    const bottomRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        bottomRef.current?.scrollIntoView({ behavior: "smooth" });
    }, [messages]);

    const sendMessage = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!input.trim() || streaming) return;

        const userMsg: Message = { role: "user", content: input };
        setMessages((prev) => [...prev, userMsg]);
        setInput("");
        setStreaming(true);

        try {
            const res = await fetch("/api/chat", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ query: input, useAi: true }),
            });

            const reader = res.body?.getReader();
            const decoder = new TextDecoder();
            let assistantContent = "";
            let sources: Message["sources"] = [];

            const assistantMsg: Message = {
                role: "assistant",
                content: "",
                sources: [],
            };
            setMessages((prev) => [...prev, assistantMsg]);

            if (reader) {
                let buffer = "";
                while (true) {
                    const { done, value } = await reader.read();
                    if (done) break;
                    buffer += decoder.decode(value, { stream: true });

                    const lines = buffer.split("\n");
                    buffer = lines.pop() ?? "";

                    for (const line of lines) {
                        if (line.startsWith("data: ")) {
                            try {
                                const data = JSON.parse(line.slice(6));
                                if (data.type === "results" && data.results) {
                                    sources = data.results.map((r: any) => ({
                                        id: r.id,
                                        name: r.originalName,
                                        category: r.category,
                                    }));
                                }
                                if (data.type === "answer" && data.text) {
                                    assistantContent = data.text;
                                }
                                if (data.type === "chunk" && data.text) {
                                    assistantContent += data.text;
                                }
                            } catch {
                                /* skip unparseable */
                            }
                        }
                    }

                    setMessages((prev) => {
                        const updated = [...prev];
                        updated[updated.length - 1] = {
                            role: "assistant",
                            content: assistantContent,
                            sources,
                        };
                        return updated;
                    });
                }
            }
        } catch (err) {
            setMessages((prev) => [
                ...prev,
                { role: "assistant", content: "Sorry, something went wrong." },
            ]);
        } finally {
            setStreaming(false);
        }
    };

    return (
        <div className="max-w-3xl mx-auto flex flex-col h-[calc(100vh-7rem)]">
            <div className="mb-4">
                <h1 className="text-xl font-bold">Chat</h1>
                <p className="text-sm text-neutral-500">
                    Ask questions about your documents
                </p>
            </div>

            {/* Messages */}
            <div className="flex-1 overflow-y-auto space-y-4 pb-4">
                {messages.length === 0 && (
                    <div className="flex items-center justify-center h-full text-neutral-600 text-sm">
                        <div className="text-center space-y-2">
                            <div className="text-2xl">💬</div>
                            <div>Ask anything about your documents</div>
                            <div className="text-xs text-neutral-700">
                                Try: "When is my insurance due?" or "Show me
                                recent invoices"
                            </div>
                        </div>
                    </div>
                )}
                {messages.map((msg, i) => (
                    <div
                        key={i}
                        className={`flex ${msg.role === "user" ? "justify-end" : "justify-start"}`}
                    >
                        <div
                            className={`max-w-[80%] rounded-xl px-4 py-3 ${
                                msg.role === "user"
                                    ? "bg-blue-600 text-white"
                                    : "bg-neutral-800 text-neutral-200"
                            }`}
                        >
                            <div className="text-sm whitespace-pre-wrap">
                                {msg.content}
                            </div>
                            {msg.sources && msg.sources.length > 0 && (
                                <div className="mt-2 pt-2 border-t border-neutral-700/50 space-y-1">
                                    <div className="text-[10px] text-neutral-500">
                                        Sources:
                                    </div>
                                    {msg.sources.map((s) => (
                                        <div
                                            key={s.id}
                                            className="text-xs text-neutral-400"
                                        >
                                            📄 {s.name}{" "}
                                            <span className="text-amber-400/60">
                                                ({s.category})
                                            </span>
                                        </div>
                                    ))}
                                </div>
                            )}
                        </div>
                    </div>
                ))}
                {streaming && (
                    <div className="text-xs text-neutral-500 animate-pulse">
                        Thinking...
                    </div>
                )}
                <div ref={bottomRef} />
            </div>

            {/* Input */}
            <form
                onSubmit={sendMessage}
                className="flex gap-3 pt-4 border-t border-neutral-800"
            >
                <input
                    type="text"
                    value={input}
                    onChange={(e) => setInput(e.target.value)}
                    placeholder="Ask about your documents..."
                    disabled={streaming}
                    className="flex-1 px-4 py-2.5 rounded-lg bg-neutral-900 border border-neutral-700 text-neutral-200 placeholder-neutral-600 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:opacity-50"
                    autoFocus
                />
                <button
                    type="submit"
                    disabled={!input.trim() || streaming}
                    className="px-6 py-2.5 rounded-lg bg-blue-600 text-white font-medium hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                    Send
                </button>
            </form>
        </div>
    );
}
