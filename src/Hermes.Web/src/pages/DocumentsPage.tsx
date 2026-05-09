import { useState, useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
    fetchTags,
    fetchDocuments,
    addDocumentTag,
    batchDocuments,
} from "../api/hermes";
import type { TagCount, DocumentSummary } from "../types/hermes";
import { DocumentRow } from "../components/documents/DocumentRow";
import { BatchBar } from "../components/documents/BatchBar";

export function DocumentsPage() {
    const [searchParams, setSearchParams] = useSearchParams();
    const selectedTag = searchParams.get("tag");
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const queryClient = useQueryClient();

    const { data: tags } = useQuery<TagCount[]>({
        queryKey: ["tags"],
        queryFn: fetchTags,
        refetchInterval: 30000,
    });

    const { data: docs, isLoading: docsLoading } = useQuery<DocumentSummary[]>(
        {
            queryKey: ["docs", selectedTag],
            queryFn: () => fetchDocuments(selectedTag ?? "", 0, 200),
            refetchInterval: 30000,
        },
    );

    const totalCount = tags?.reduce((sum, t) => sum + t.count, 0) ?? 0;
    const sorted = (tags ?? []).slice().sort((a, b) => b.count - a.count);

    const selectTag = useCallback(
        (tag: string | null) => {
            setSelectedIds(new Set());
            if (tag) {
                setSearchParams({ tag });
            } else {
                setSearchParams({});
            }
        },
        [setSearchParams],
    );

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
            queryClient.invalidateQueries({ queryKey: ["docs"] });
        },
        [selectedIds, queryClient],
    );

    const tagButtonClass = (active: boolean) =>
        `w-full text-left px-3 py-2 rounded-lg text-sm transition-colors ${
            active
                ? "bg-amber-500/10 text-amber-600 dark:text-amber-400 ring-1 ring-amber-500/30"
                : "text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800/50 hover:text-neutral-900 dark:hover:text-neutral-200"
        }`;

    return (
        <div className="flex gap-6 h-[calc(100vh-7rem)]">
            {/* Desktop: Tag panel */}
            <div className="w-48 shrink-0 overflow-y-auto hidden md:block">
                <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-3">
                    TAGS
                </div>
                <div className="space-y-0.5">
                    <button
                        onClick={() => selectTag(null)}
                        className={tagButtonClass(!selectedTag)}
                    >
                        <div className="flex justify-between items-center">
                            <span>All</span>
                            <span className="font-mono text-xs text-neutral-500">
                                {totalCount}
                            </span>
                        </div>
                    </button>

                    {sorted.map((t) => (
                        <button
                            key={t.tag}
                            onClick={() => selectTag(t.tag)}
                            className={tagButtonClass(selectedTag === t.tag)}
                        >
                            <div className="flex justify-between items-center">
                                <span className="truncate">{t.tag}</span>
                                <span className="font-mono text-xs text-neutral-500">
                                    {t.count}
                                </span>
                            </div>
                        </button>
                    ))}

                    {sorted.length === 0 && (
                        <div className="text-xs text-neutral-500 py-4">
                            No tags yet
                        </div>
                    )}
                </div>
            </div>

            {/* Right: Document list */}
            <div className="flex-1 min-w-0 rounded-xl border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900/50 overflow-hidden flex flex-col">
                {/* Mobile: Tag select */}
                <div className="md:hidden px-4 py-2 border-b border-neutral-200 dark:border-neutral-800">
                    <select
                        value={selectedTag ?? ""}
                        onChange={(e) => selectTag(e.target.value || null)}
                        className="w-full px-3 py-2 rounded-lg bg-white dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-700 text-sm text-neutral-900 dark:text-neutral-200"
                    >
                        <option value="">All ({totalCount})</option>
                        {sorted.map((t) => (
                            <option key={t.tag} value={t.tag}>
                                {t.tag} ({t.count})
                            </option>
                        ))}
                    </select>
                </div>

                {/* Header */}
                <div className="px-4 py-3 border-b border-neutral-200 dark:border-neutral-800">
                    <span className="text-sm font-semibold text-neutral-700 dark:text-neutral-300">
                        {selectedTag
                            ? `🏷️ ${selectedTag}`
                            : "📄 All documents"}
                    </span>
                    {docs && (
                        <span className="text-xs text-neutral-500 ml-2">
                            ({docs.length})
                        </span>
                    )}
                </div>

                {/* Document rows */}
                <div className="flex-1 overflow-y-auto">
                    {docsLoading && (
                        <div className="flex items-center justify-center h-32 text-sm text-neutral-500 animate-pulse">
                            Loading…
                        </div>
                    )}
                    {!docsLoading && docs?.length === 0 && (
                        <div className="flex items-center justify-center h-32 text-sm text-neutral-500">
                            No documents found
                        </div>
                    )}
                    {docs?.map((doc) => (
                        <DocumentRow
                            key={doc.id}
                            doc={doc}
                            selected={selectedIds.has(doc.id)}
                            onToggle={toggleDoc}
                        />
                    ))}
                </div>

                {/* Batch operations */}
                <BatchBar
                    selectedCount={selectedIds.size}
                    onTag={handleBatchTag}
                    onClearSelection={() => setSelectedIds(new Set())}
                />
            </div>
        </div>
    );
}
