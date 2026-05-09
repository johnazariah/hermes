import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
    fetchSuggestions,
    approveSuggestion,
    rejectSuggestion,
    fetchRecentDocuments,
} from "../api/hermes";
import type { Suggestion, DocumentSummary } from "../types/hermes";

// ── Date grouping ────────────────────────────────────────────

function groupLabel(dateStr: string): string {
    const date = new Date(dateStr);
    const now = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const yesterday = new Date(today);
    yesterday.setDate(yesterday.getDate() - 1);
    const weekAgo = new Date(today);
    weekAgo.setDate(weekAgo.getDate() - 7);

    if (date >= today) return "Today";
    if (date >= yesterday) return "Yesterday";
    if (date >= weekAgo) return "This Week";
    return "Earlier";
}

function groupDocumentsByDate(
    docs: DocumentSummary[],
): { label: string; docs: DocumentSummary[] }[] {
    const order = ["Today", "Yesterday", "This Week", "Earlier"];
    const groups = new Map<string, DocumentSummary[]>();
    for (const doc of docs) {
        const label = groupLabel(doc.extractedDate ?? "");
        const list = groups.get(label) ?? [];
        list.push(doc);
        groups.set(label, list);
    }
    return order
        .filter((l) => groups.has(l))
        .map((label) => ({ label, docs: groups.get(label)! }));
}

// ── Confidence badge ─────────────────────────────────────────

function ConfidenceBadge({ value }: { value: number }) {
    const pct = Math.round(value * 100);
    const color =
        pct >= 90
            ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300"
            : pct >= 70
              ? "bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300"
              : "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300";

    return (
        <span
            className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${color}`}
        >
            {pct}%
        </span>
    );
}

// ── Category tag ─────────────────────────────────────────────

function CategoryTag({ category }: { category: string }) {
    return (
        <span className="inline-flex items-center rounded-md bg-neutral-100 px-2 py-0.5 text-xs font-medium text-neutral-600 dark:bg-neutral-800 dark:text-neutral-400">
            {category}
        </span>
    );
}

// ── Triage card ──────────────────────────────────────────────

function TriageCard({
    suggestion,
    onApprove,
    onReject,
    onSkip,
    isApproving,
    isRejecting,
}: {
    suggestion: Suggestion;
    onApprove: () => void;
    onReject: () => void;
    onSkip: () => void;
    isApproving: boolean;
    isRejecting: boolean;
}) {
    const name = suggestion.originalName ?? `Document #${suggestion.id}`;
    const pct = Math.round(suggestion.confidence * 100);

    return (
        <div className="rounded-xl border border-amber-200 bg-amber-50/50 p-4 dark:border-amber-800/50 dark:bg-amber-950/20">
            <div className="flex items-start justify-between gap-3">
                <div className="min-w-0 flex-1">
                    <p className="truncate font-medium text-neutral-900 dark:text-neutral-100">
                        📄 {name}
                    </p>
                    <p className="mt-1 text-sm text-neutral-600 dark:text-neutral-400">
                        Hermes thinks:{" "}
                        <span className="font-medium">
                            {suggestion.proposedCategory}
                        </span>{" "}
                        ({pct}%)
                    </p>
                    {suggestion.sender && (
                        <p className="mt-0.5 text-sm text-neutral-500 dark:text-neutral-500">
                            From: {suggestion.sender}
                        </p>
                    )}
                </div>
            </div>
            <div className="mt-3 flex items-center gap-2">
                <button
                    onClick={onApprove}
                    disabled={isApproving}
                    className="inline-flex items-center gap-1 rounded-lg bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-700 disabled:opacity-50 dark:bg-emerald-700 dark:hover:bg-emerald-600"
                >
                    ✓ Accept
                </button>
                <button
                    onClick={onReject}
                    disabled={isRejecting}
                    className="inline-flex items-center gap-1 rounded-lg bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 dark:bg-red-700 dark:hover:bg-red-600"
                >
                    ✗ Reject
                </button>
                <button
                    onClick={onSkip}
                    className="inline-flex items-center gap-1 rounded-lg bg-neutral-200 px-3 py-1.5 text-sm font-medium text-neutral-700 hover:bg-neutral-300 dark:bg-neutral-700 dark:text-neutral-300 dark:hover:bg-neutral-600"
                >
                    Skip
                </button>
            </div>
        </div>
    );
}

// ── Triage panel ─────────────────────────────────────────────

function TriagePanel() {
    const queryClient = useQueryClient();
    const [skippedIds, setSkippedIds] = useState<Set<number>>(new Set());

    const { data: suggestions = [], isLoading } = useQuery({
        queryKey: ["suggestions"],
        queryFn: fetchSuggestions,
        refetchInterval: 10_000,
    });

    const approveMutation = useMutation({
        mutationFn: approveSuggestion,
        onMutate: async (id: number) => {
            await queryClient.cancelQueries({ queryKey: ["suggestions"] });
            const previous =
                queryClient.getQueryData<Suggestion[]>(["suggestions"]);
            queryClient.setQueryData<Suggestion[]>(["suggestions"], (old) =>
                (old ?? []).filter((s) => s.id !== id),
            );
            return { previous };
        },
        onError: (_err, _id, context) => {
            if (context?.previous)
                queryClient.setQueryData(["suggestions"], context.previous);
        },
        onSettled: () => {
            queryClient.invalidateQueries({ queryKey: ["suggestions"] });
            queryClient.invalidateQueries({ queryKey: ["recent-documents"] });
        },
    });

    const rejectMutation = useMutation({
        mutationFn: rejectSuggestion,
        onMutate: async (id: number) => {
            await queryClient.cancelQueries({ queryKey: ["suggestions"] });
            const previous =
                queryClient.getQueryData<Suggestion[]>(["suggestions"]);
            queryClient.setQueryData<Suggestion[]>(["suggestions"], (old) =>
                (old ?? []).filter((s) => s.id !== id),
            );
            return { previous };
        },
        onError: (_err, _id, context) => {
            if (context?.previous)
                queryClient.setQueryData(["suggestions"], context.previous);
        },
        onSettled: () => {
            queryClient.invalidateQueries({ queryKey: ["suggestions"] });
        },
    });

    const pending = suggestions.filter(
        (s) => s.status === "pending" && !skippedIds.has(s.id),
    );

    if (isLoading) return null;

    return (
        <section className="mb-8">
            <h2 className="mb-3 text-lg font-semibold text-neutral-900 dark:text-neutral-100">
                Review{" "}
                {pending.length > 0 && (
                    <span className="ml-1 inline-flex items-center justify-center rounded-full bg-amber-500 px-2 py-0.5 text-xs font-bold text-white">
                        {pending.length}
                    </span>
                )}
            </h2>
            {pending.length === 0 ? (
                <div className="rounded-xl border border-neutral-200 bg-neutral-50 p-8 text-center dark:border-neutral-800 dark:bg-neutral-900/50">
                    <p className="text-2xl">🎉</p>
                    <p className="mt-2 font-medium text-neutral-600 dark:text-neutral-400">
                        All caught up!
                    </p>
                </div>
            ) : (
                <div className="space-y-3">
                    {pending.map((s) => (
                        <TriageCard
                            key={s.id}
                            suggestion={s}
                            onApprove={() => approveMutation.mutate(s.id)}
                            onReject={() => rejectMutation.mutate(s.id)}
                            onSkip={() =>
                                setSkippedIds((prev) =>
                                    new Set(prev).add(s.id),
                                )
                            }
                            isApproving={
                                approveMutation.isPending &&
                                approveMutation.variables === s.id
                            }
                            isRejecting={
                                rejectMutation.isPending &&
                                rejectMutation.variables === s.id
                            }
                        />
                    ))}
                </div>
            )}
        </section>
    );
}

// ── Document row ─────────────────────────────────────────────

function DocumentRow({ doc }: { doc: DocumentSummary }) {
    const amount = doc.extractedAmount;
    const formatted =
        amount != null
            ? amount.toLocaleString("en-AU", {
                  style: "currency",
                  currency: "AUD",
              })
            : null;

    return (
        <div className="flex items-center gap-3 rounded-lg px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-800/60">
            <span className="text-base">📄</span>
            <span className="min-w-0 flex-1 truncate text-sm font-medium text-neutral-900 dark:text-neutral-100">
                {doc.originalName}
            </span>
            <CategoryTag category={doc.category} />
            {formatted && (
                <span className="text-sm tabular-nums text-neutral-600 dark:text-neutral-400">
                    {formatted}
                </span>
            )}
            {doc.classificationConfidence != null && (
                <ConfidenceBadge value={doc.classificationConfidence} />
            )}
        </div>
    );
}

// ── Document feed ────────────────────────────────────────────

function DocumentFeed() {
    const { data: docs = [], isLoading } = useQuery({
        queryKey: ["recent-documents"],
        queryFn: () => fetchRecentDocuments(20),
        refetchInterval: 10_000,
    });

    const groups = groupDocumentsByDate(docs);

    return (
        <section>
            <div className="mb-3 flex items-center justify-between">
                <h2 className="text-lg font-semibold text-neutral-900 dark:text-neutral-100">
                    Recent
                </h2>
            </div>
            {isLoading ? (
                <div className="rounded-xl border border-neutral-200 bg-neutral-50 p-8 text-center dark:border-neutral-800 dark:bg-neutral-900/50">
                    <p className="text-sm text-neutral-500">
                        Loading documents…
                    </p>
                </div>
            ) : docs.length === 0 ? (
                <div className="rounded-xl border border-neutral-200 bg-neutral-50 p-8 text-center dark:border-neutral-800 dark:bg-neutral-900/50">
                    <p className="text-sm text-neutral-500">
                        No documents yet. Drop files into your archive or
                        connect an email account to get started.
                    </p>
                </div>
            ) : (
                <div className="rounded-xl border border-neutral-200 bg-white dark:border-neutral-800 dark:bg-neutral-900/50">
                    {groups.map((group) => (
                        <div key={group.label}>
                            <div className="px-3 pt-3 pb-1">
                                <span className="text-[10px] font-semibold uppercase tracking-widest text-neutral-500">
                                    {group.label}
                                </span>
                            </div>
                            {group.docs.map((doc) => (
                                <DocumentRow key={doc.id} doc={doc} />
                            ))}
                        </div>
                    ))}
                </div>
            )}
        </section>
    );
}

// ── Home page ────────────────────────────────────────────────

export function HomePage() {
    return (
        <div className="mx-auto max-w-4xl">
            <TriagePanel />
            <DocumentFeed />
        </div>
    );
}
