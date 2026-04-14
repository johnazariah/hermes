import { useState } from "react";
import { useQuery } from "@tanstack/react-query";

interface SearchResult {
    id: number;
    originalName: string;
    category: string;
    sender: string | null;
    snippet: string;
    score: number;
}

async function searchDocs(query: string): Promise<SearchResult[]> {
    const res = await fetch(
        `/api/search?q=${encodeURIComponent(query)}&limit=50`,
    );
    return res.json();
}

export function SearchPage() {
    const [query, setQuery] = useState("");
    const [submitted, setSubmitted] = useState("");

    const { data: results, isLoading } = useQuery({
        queryKey: ["search", submitted],
        queryFn: () => searchDocs(submitted),
        enabled: submitted.length > 0,
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setSubmitted(query);
    };

    return (
        <div className="max-w-4xl mx-auto space-y-6">
            <div>
                <h1 className="text-xl font-bold mb-1">Search</h1>
                <p className="text-sm text-neutral-500">
                    Find documents by keyword or semantic similarity
                </p>
            </div>

            <form onSubmit={handleSubmit} className="flex gap-3">
                <input
                    type="text"
                    value={query}
                    onChange={(e) => setQuery(e.target.value)}
                    placeholder="Search your documents..."
                    className="flex-1 px-4 py-2.5 rounded-lg bg-neutral-900 border border-neutral-700 text-neutral-200 placeholder-neutral-600 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    autoFocus
                />
                <button
                    type="submit"
                    disabled={!query.trim()}
                    className="px-6 py-2.5 rounded-lg bg-blue-600 text-white font-medium hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                    Search
                </button>
            </form>

            {isLoading && (
                <div className="text-sm text-neutral-500 animate-pulse">
                    Searching...
                </div>
            )}

            {results && results.length === 0 && (
                <div className="text-sm text-neutral-500">
                    No results found for "{submitted}"
                </div>
            )}

            {results && results.length > 0 && (
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 overflow-hidden">
                    <div className="px-4 py-2 border-b border-neutral-800 text-xs text-neutral-500">
                        {results.length} results
                    </div>
                    {results.map((r) => (
                        <div
                            key={r.id}
                            className="px-4 py-3 border-b border-neutral-800/50 hover:bg-neutral-800/30 transition-colors"
                        >
                            <div className="text-sm text-neutral-200">
                                {r.originalName}
                            </div>
                            <div className="flex gap-3 mt-0.5 text-xs">
                                <span className="text-amber-400/70">
                                    {r.category}
                                </span>
                                {r.sender && (
                                    <span className="text-neutral-500">
                                        {r.sender}
                                    </span>
                                )}
                                <span className="text-neutral-600 ml-auto">
                                    score: {r.score.toFixed(2)}
                                </span>
                            </div>
                            {r.snippet && (
                                <div className="mt-1 text-xs text-neutral-400 line-clamp-2">
                                    {r.snippet}
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
}
