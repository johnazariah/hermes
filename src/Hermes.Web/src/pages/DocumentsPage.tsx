import { useState, useCallback, useEffect, useRef } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
    fetchCategories,
    fetchTags,
    fetchDocuments,
    addDocumentTag,
    batchDocuments,
    reclassifyDocuments,
} from "../api/hermes";
import type {
    BatchReclassificationResponse,
    CategoryCount,
    TagCount,
    DocumentSummary,
} from "../types/hermes";
import { DocumentRow } from "../components/documents/DocumentRow";
import { BatchBar } from "../components/documents/BatchBar";

type ReclassificationResult =
    | {
          category: string;
          response: BatchReclassificationResponse;
          error?: never;
      }
    | { category: string; error: string; response?: never };

function ReclassificationFeedback({
    result,
}: {
    result: ReclassificationResult;
}) {
    if ("error" in result) {
        return (
            <div
                role="alert"
                className="border-b border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300"
            >
                Reclassification failed: {result.error}
            </div>
        );
    }

    const response = result.response;
    const changed = response.outcomes.filter(
        (outcome) => outcome.changed,
    );
    const unchanged = response.outcomes.filter(
        (outcome) => outcome.status === "unchanged",
    );
    const failed = response.outcomes.filter(
        (outcome) => outcome.status === "failed",
    );
    const completed = changed.length + unchanged.length;
    const isPartial = failed.length > 0 && completed > 0;

    let summary: string;
    if (failed.length === response.outcomes.length) {
        summary = `Reclassification failed for ${failed.length} document${failed.length === 1 ? "" : "s"}.`;
    } else if (isPartial) {
        summary = `Reclassification partially completed: ${changed.length} changed, ${unchanged.length} unchanged, ${failed.length} failed.`;
    } else if (changed.length > 0 && unchanged.length > 0) {
        summary = `Reclassification completed: ${changed.length} changed and ${unchanged.length} unchanged.`;
    } else if (changed.length > 0) {
        summary = `Reclassified ${changed.length} document${changed.length === 1 ? "" : "s"} to “${result.category}”.`;
    } else {
        summary = `No changes: ${unchanged.length} document${unchanged.length === 1 ? " was" : "s were"} already categorized as “${result.category}”.`;
    }

    return (
        <div
            role={failed.length > 0 ? "alert" : "status"}
            className={`border-b px-4 py-3 text-sm ${
                failed.length > 0
                    ? "border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-300"
                    : "border-green-200 bg-green-50 text-green-900 dark:border-green-900 dark:bg-green-950/40 dark:text-green-300"
            }`}
        >
            <div className="font-medium">{summary}</div>
            <ul className="mt-1 space-y-0.5 text-xs">
                {response.outcomes.map((outcome) => (
                    <li key={outcome.documentId}>
                        Document {outcome.documentId}:{" "}
                        {outcome.status === "failed"
                            ? outcome.error ?? "Reclassification failed"
                            : outcome.changed
                              ? `reclassified from “${outcome.previousCategory ?? "uncategorized"}” to “${outcome.category ?? result.category}”`
                              : `already categorized as “${outcome.category ?? result.category}”`}
                    </li>
                ))}
            </ul>
        </div>
    );
}

export function DocumentsPage() {
    const [searchParams, setSearchParams] = useSearchParams();
    const selectedTag = searchParams.get("tag");
    const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
    const [reclassificationResult, setReclassificationResult] =
        useState<ReclassificationResult | null>(null);
    const activeFilterRef = useRef(selectedTag);
    const previousFilterRef = useRef(selectedTag);
    const queryClient = useQueryClient();

    useEffect(() => {
        activeFilterRef.current = selectedTag;
        if (previousFilterRef.current !== selectedTag) {
            previousFilterRef.current = selectedTag;
            setSelectedIds(new Set());
        }
    }, [selectedTag]);

    const { data: tags } = useQuery<TagCount[]>({
        queryKey: ["tags"],
        queryFn: fetchTags,
        refetchInterval: 30000,
    });

    const { data: categories } = useQuery<CategoryCount[]>({
        queryKey: ["categories"],
        queryFn: fetchCategories,
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

    const reclassificationMutation = useMutation({
        mutationFn: (request: {
            ids: number[];
            category: string;
            filter: string | null;
        }) => reclassifyDocuments(request.ids, request.category),
        onMutate: () => setReclassificationResult(null),
        onSuccess: (response, request) => {
            const filterUnchanged =
                activeFilterRef.current === request.filter;
            if (response.outcomes.length === 0) {
                if (!filterUnchanged) setSelectedIds(new Set());
                setReclassificationResult({
                    category: request.category,
                    error: "The service returned no per-document outcomes.",
                });
                return;
            }
            setReclassificationResult({
                category: request.category,
                response,
            });
            const failedIds = response.outcomes
                .filter((outcome) => outcome.status === "failed")
                .map((outcome) => outcome.documentId);
            setSelectedIds(new Set(filterUnchanged ? failedIds : []));

            if (response.outcomes.some((outcome) => outcome.changed)) {
                queryClient.invalidateQueries({ queryKey: ["docs"] });
                queryClient.invalidateQueries({ queryKey: ["documents"] });
                queryClient.invalidateQueries({ queryKey: ["document"] });
                queryClient.invalidateQueries({ queryKey: ["doc-detail"] });
                queryClient.invalidateQueries({ queryKey: ["recent-documents"] });
                queryClient.invalidateQueries({ queryKey: ["search"] });
                queryClient.invalidateQueries({ queryKey: ["tags"] });
                queryClient.invalidateQueries({ queryKey: ["categories"] });
                queryClient.invalidateQueries({ queryKey: ["activity"] });
            }
        },
        onError: (error, request) => {
            if (activeFilterRef.current !== request.filter) {
                setSelectedIds(new Set());
            }
            setReclassificationResult({
                category: request.category,
                error:
                    error instanceof Error
                        ? error.message
                        : "Reclassification request failed",
            });
        },
    });

    const selectTag = useCallback(
        (tag: string | null) => {
            if (reclassificationMutation.isPending) return;
            setSelectedIds(new Set());
            if (tag) {
                setSearchParams({ tag });
            } else {
                setSearchParams({});
            }
        },
        [reclassificationMutation.isPending, setSearchParams],
    );

    const toggleDoc = useCallback((id: number) => {
        if (reclassificationMutation.isPending) return;
        setSelectedIds((prev) => {
            const next = new Set(prev);
            if (next.has(id)) next.delete(id);
            else next.add(id);
            return next;
        });
    }, [reclassificationMutation.isPending]);

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

    const handleReclassify = useCallback(
        (category: string) => {
            const ids = Array.from(selectedIds);
            const requestedCategory = category.trim();
            if (
                ids.length === 0 ||
                !requestedCategory ||
                reclassificationMutation.isPending
            )
                return;
            reclassificationMutation.mutate({
                ids,
                category: requestedCategory,
                filter: selectedTag,
            });
        },
        [selectedIds, selectedTag, reclassificationMutation],
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
                        disabled={reclassificationMutation.isPending}
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
                            disabled={reclassificationMutation.isPending}
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
                        disabled={reclassificationMutation.isPending}
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
                            disabled={reclassificationMutation.isPending}
                        />
                    ))}
                </div>

                {reclassificationResult && (
                    <ReclassificationFeedback result={reclassificationResult} />
                )}

                {/* Batch operations */}
                <BatchBar
                    selectedCount={selectedIds.size}
                    onTag={handleBatchTag}
                    onReclassify={handleReclassify}
                    categories={(categories ?? []).map(
                        (item) => item.category,
                    )}
                    reclassificationPending={
                        reclassificationMutation.isPending
                    }
                    onClearSelection={() => setSelectedIds(new Set())}
                />
            </div>
        </div>
    );
}
