import { useState, useEffect, useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
    fetchTags,
    searchDocuments,
    addDocumentTag,
    batchDocuments,
} from "../api/hermes";
import type { TagCount, DocumentSummary } from "../types/hermes";
import { DocumentRow } from "../components/documents/DocumentRow";
import { BatchBar } from "../components/documents/BatchBar";

const DATE_RANGES = [
    { label: "Any time", value: "" },
    { label: "Last 7 days", value: "7" },
    { label: "Last 30 days", value: "30" },
    { label: "Last 90 days", value: "90" },
    { label: "Last year", value: "365" },
] as const;

export function SearchPage() {
    const [searchParams, setSearchParams] = useSearchParams();
    const queryFromUrl = searchParams.get("q") ?? "";
    const tagFromUrl = searchParams.get("tag") ?? "";
    const dateRangeFromUrl = searchParams.get("days") ?? "";

    const [query, setQuery] = useState(queryFromUrl);
    const [debouncedQuery, setDebouncedQuery] = useState(queryFromUrl);
    const [selectedTag, setSelectedTag] = useState(tagFromUrl);
    const [dateRange, setDateRange] = useState(dateRangeFromUrl);
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const queryClient = useQueryClient();

    // Debounce search query
    useEffect(() => {
        const timer = setTimeout(() => setDebouncedQuery(query), 300);
        return () => clearTimeout(timer);
    }, [query]);

    // Update URL when search state changes
    useEffect(() => {
        const params: Record<string, string> = {};
        if (debouncedQuery) params.q = debouncedQuery;
        if (selectedTag) params.tag = selectedTag;
        if (dateRange) params.days = dateRange;
        setSearchParams(params, { replace: true });
    }, [debouncedQuery, selectedTag, dateRange, setSearchParams]);

    const { data: tags } = useQuery<TagCount[]>({
        queryKey: ["tags"],
        queryFn: fetchTags,
        refetchInterval: 30000,
    });

    const hasFilters =
        debouncedQuery.length > 0 || selectedTag.length > 0;

    const { data: results, isLoading } = useQuery<DocumentSummary[]>({
        queryKey: ["search", debouncedQuery, selectedTag, dateRange],
        queryFn: () =>
            searchDocuments({
                q: debouncedQuery || undefined,
                category: selectedTag || undefined,
                limit: 50,
            }),
        enabled: hasFilters,
    });

    const toggleDoc = useCallback((id: number) => {
        setSelectedIds((prev) => {
            const next = new Set(prev);
            if (next.has(id)) next.delete(id);
            else next.add(id);
            return next;
        });
    }, []);

    const handleBatchTag = useCallback(
        async (tag: string) => {
            const ids = Array.from(selectedIds);
            if (ids.length === 0) return;
            try {
                await batchDocuments(ids, "tag", tag);
            } catch {
                for (const id of ids) {
                    await addDocumentTag(id, tag);
                }
            }
            setSelectedIds(new Set());
            queryClient.invalidateQueries({ queryKey: ["tags"] });
            queryClient.invalidateQueries({ queryKey: ["search"] });
        },
        [selectedIds, queryClient],
    );

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setDebouncedQuery(query);
    };

    const sortedTags = (tags ?? []).slice().sort((a, b) => b.count - a.count);

    return (
        <div className="max-w-4xl mx-auto space-y-4">
            {/* Header */}
            <div>
                <h1 className="text-xl font-bold mb-1">Search</h1>
                <p className="text-sm text-neutral-500">
                    Find documents by keyword or semantic similarity
                </p>
            </div>

            {/* Search bar */}
            <form onSubmit={handleSubmit} className="flex gap-3">
                <div className="relative flex-1">
                    <span className="absolute left-3 top-1/2 -translate-y-1/2 text-neutral-400">
                        🔍
                    </span>
                    <input
                        type="text"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                        placeholder="Search your documents..."
                        className="w-full pl-10 pr-4 py-2.5 rounded-lg bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-700 text-neutral-900 dark:text-neutral-200 placeholder-neutral-400 dark:placeholder-neutral-600 focus:outline-none focus:ring-1 focus:ring-blue-500"
                        autoFocus
                    />
                </div>
                <button
                    type="submit"
                    disabled={!query.trim()}
                    className="px-6 py-2.5 rounded-lg bg-blue-600 text-white font-medium hover:bg-blue-500 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                    Search
                </button>
            </form>

            {/* Facet filters */}
            <div className="flex flex-wrap gap-3 items-center">
                <span className="text-xs font-medium text-neutral-500">
                    Filters:
                </span>

                <select
                    value={selectedTag}
                    onChange={(e) => {
                        setSelectedTag(e.target.value);
                        setSelectedIds(new Set());
                    }}
                    className="px-3 py-1.5 text-sm rounded-lg bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-700 text-neutral-900 dark:text-neutral-200"
                >
                    <option value="">All tags</option>
                    {sortedTags.map((t) => (
                        <option key={t.tag} value={t.tag}>
                            {t.tag} ({t.count})
                        </option>
                    ))}
                </select>

                <select
                    value={dateRange}
                    onChange={(e) => setDateRange(e.target.value)}
                    className="px-3 py-1.5 text-sm rounded-lg bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-700 text-neutral-900 dark:text-neutral-200"
                >
                    {DATE_RANGES.map((d) => (
                        <option key={d.value} value={d.value}>
                            {d.label}
                        </option>
                    ))}
                </select>
            </div>

            {/* Results */}
            {isLoading && (
                <div className="text-sm text-neutral-500 animate-pulse">
                    Searching…
                </div>
            )}

            {!hasFilters && (
                <div className="text-sm text-neutral-500 py-8 text-center">
                    Enter a search query or select a tag to find documents
                </div>
            )}

            {hasFilters && !isLoading && results && results.length === 0 && (
                <div className="text-sm text-neutral-500">
                    No results found
                    {debouncedQuery && (
                        <span>
                            {" "}
                            for &ldquo;{debouncedQuery}&rdquo;
                        </span>
                    )}
                </div>
            )}

            {results && results.length > 0 && (
                <div className="rounded-xl border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900/50 overflow-hidden">
                    <div className="px-4 py-2 border-b border-neutral-200 dark:border-neutral-800 text-xs text-neutral-500">
                        {results.length} result{results.length !== 1 && "s"}
                    </div>
                    {results.map((doc) => (
                        <DocumentRow
                            key={doc.id}
                            doc={doc}
                            selected={selectedIds.has(doc.id)}
                            onToggle={toggleDoc}
                        />
                    ))}
                    <BatchBar
                        selectedCount={selectedIds.size}
                        onTag={handleBatchTag}
                        onClearSelection={() => setSelectedIds(new Set())}
                    />
                </div>
            )}
        </div>
    );
}
