import { useState, useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import Markdown from "react-markdown";
import { fetchCategories, fetchDocuments } from "../api/hermes";
import type { CategoryCount, DocumentSummary } from "../types/hermes";

async function fetchDocDetail(id: number) {
    const res = await fetch(`/api/documents/${id}`);
    return res.json();
}

async function fetchDocContent(id: number) {
    const res = await fetch(`/api/documents/${id}/content`);
    return res.json();
}

export function DocumentsPage() {
    const [selectedCategory, setSelectedCategory] = useState<string | null>(
        null,
    );
    const [selectedDocId, setSelectedDocId] = useState<number | null>(null);

    const { data: categories } = useQuery({
        queryKey: ["categories"],
        queryFn: fetchCategories,
        refetchInterval: 30000,
    });

    const { data: docs } = useQuery({
        queryKey: ["docs", selectedCategory],
        queryFn: () => fetchDocuments(selectedCategory ?? "", 0, 200),
        enabled: selectedCategory != null,
        refetchInterval: 30000,
    });

    const { data: detail } = useQuery({
        queryKey: ["doc-detail", selectedDocId],
        queryFn: () => fetchDocDetail(selectedDocId!),
        enabled: selectedDocId != null,
    });

    const { data: content } = useQuery({
        queryKey: ["doc-content", selectedDocId],
        queryFn: () => fetchDocContent(selectedDocId!),
        enabled: selectedDocId != null,
    });

    const sorted = (categories ?? []).sort(
        (a: CategoryCount, b: CategoryCount) => b.count - a.count,
    );

    return (
        <div className="flex gap-6 h-[calc(100vh-7rem)]">
            {/* Left: Category grid */}
            <div className="w-56 shrink-0 overflow-y-auto">
                <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-3">
                    CATEGORIES
                </div>
                <div className="space-y-0.5">
                    {sorted.map((cat: CategoryCount) => (
                        <button
                            key={cat.category}
                            onClick={() => {
                                setSelectedCategory(cat.category);
                                setSelectedDocId(null);
                            }}
                            className={`w-full text-left px-3 py-2 rounded-lg text-sm transition-colors ${
                                selectedCategory === cat.category
                                    ? "bg-amber-500/10 text-amber-400 ring-1 ring-amber-500/30"
                                    : "text-neutral-400 hover:bg-neutral-800/50 hover:text-neutral-200"
                            }`}
                        >
                            <div className="flex justify-between items-center">
                                <span className="truncate">
                                    📁 {cat.category}
                                </span>
                                <span className="font-mono text-xs text-neutral-500">
                                    {cat.count}
                                </span>
                            </div>
                        </button>
                    ))}
                    {sorted.length === 0 && (
                        <div className="text-xs text-neutral-600 py-4">
                            No categories yet
                        </div>
                    )}
                </div>
            </div>

            {/* Center: Document list */}
            <div className="flex-1 min-w-0 rounded-xl border border-neutral-800 bg-neutral-900/50 overflow-hidden flex flex-col">
                <div className="px-4 py-3 border-b border-neutral-800">
                    <span className="text-sm font-semibold text-neutral-300">
                        {selectedCategory
                            ? `📁 ${selectedCategory}`
                            : "Select a category"}
                    </span>
                    {docs && (
                        <span className="text-xs text-neutral-500 ml-2">
                            ({docs.length})
                        </span>
                    )}
                </div>
                <div className="flex-1 overflow-y-auto">
                    {!selectedCategory && (
                        <div className="flex items-center justify-center h-full text-neutral-600 text-sm">
                            Choose a category to browse documents
                        </div>
                    )}
                    {docs?.map((doc: DocumentSummary) => (
                        <button
                            key={doc.id}
                            onClick={() => setSelectedDocId(doc.id)}
                            className={`w-full text-left px-4 py-2.5 border-b border-neutral-800/50 hover:bg-neutral-800/30 transition-colors ${
                                selectedDocId === doc.id
                                    ? "bg-neutral-800/50"
                                    : ""
                            }`}
                        >
                            <div className="text-sm text-neutral-200 truncate">
                                {doc.originalName}
                            </div>
                            <div className="flex gap-3 mt-0.5 text-xs text-neutral-500">
                                {doc.sender && (
                                    <span className="truncate">
                                        {doc.sender}
                                    </span>
                                )}
                                {doc.extractedDate && (
                                    <span>{doc.extractedDate}</span>
                                )}
                                {doc.extractedAmount != null && (
                                    <span className="text-green-400">
                                        ${doc.extractedAmount.toFixed(2)}
                                    </span>
                                )}
                            </div>
                        </button>
                    ))}
                </div>
            </div>

            {/* Right: Document detail — split view with PDF + markdown */}
            {selectedDocId && detail ? (
                <div className="flex-1 min-w-0 rounded-xl border border-neutral-800 bg-neutral-900/50 overflow-hidden flex flex-col">
                    {/* Header */}
                    <div className="px-4 py-3 border-b border-neutral-800 flex items-center gap-3">
                        <button
                            onClick={() => setSelectedDocId(null)}
                            className="text-neutral-500 hover:text-white text-sm"
                        >
                            ← Back
                        </button>
                        <div className="flex-1 min-w-0">
                            <div className="text-sm font-semibold text-neutral-200 truncate">
                                {detail.summary?.originalName}
                            </div>
                            <div className="text-xs text-neutral-500">
                                {detail.summary?.sourceType} ·{" "}
                                {detail.summary?.category}
                                {detail.summary?.classificationTier &&
                                    ` · ${detail.summary.classificationTier}`}
                                {detail.summary?.extractedAmount != null && (
                                    <span className="text-green-400 ml-2">
                                        $
                                        {detail.summary.extractedAmount.toFixed(
                                            2,
                                        )}
                                    </span>
                                )}
                            </div>
                        </div>
                    </div>

                    {/* Split: Original file + Extracted content side by side */}
                    <div className="flex-1 flex overflow-hidden">
                        {/* Left: Original file — only show if file exists */}
                        <OriginalFilePanel
                            docId={selectedDocId}
                            name={detail.summary?.originalName}
                        />

                        {/* Right: Extracted markdown */}
                        <div className="flex-1 flex flex-col overflow-hidden">
                            <div className="px-3 py-2 border-b border-neutral-800/50 text-[10px] font-semibold tracking-widest text-neutral-500">
                                EXTRACTED CONTENT
                            </div>
                            <div className="flex-1 overflow-y-auto p-4">
                                {/* Metadata */}
                                <div className="space-y-1.5 text-xs mb-4">
                                    {detail.summary?.sender && (
                                        <div>
                                            <span className="text-neutral-600 w-16 inline-block">
                                                From
                                            </span>{" "}
                                            <span className="text-neutral-300">
                                                {detail.summary.sender}
                                            </span>
                                        </div>
                                    )}
                                    {detail.summary?.extractedDate && (
                                        <div>
                                            <span className="text-neutral-600 w-16 inline-block">
                                                Date
                                            </span>{" "}
                                            <span className="text-neutral-300">
                                                {detail.summary.extractedDate}
                                            </span>
                                        </div>
                                    )}
                                    {detail.summary?.extractedAmount !=
                                        null && (
                                        <div>
                                            <span className="text-neutral-600 w-16 inline-block">
                                                Amount
                                            </span>{" "}
                                            <span className="text-green-400">
                                                $
                                                {detail.summary.extractedAmount.toFixed(
                                                    2,
                                                )}
                                            </span>
                                        </div>
                                    )}
                                    {detail.summary?.vendor && (
                                        <div>
                                            <span className="text-neutral-600 w-16 inline-block">
                                                Vendor
                                            </span>{" "}
                                            <span className="text-neutral-300">
                                                {detail.summary.vendor}
                                            </span>
                                        </div>
                                    )}
                                </div>

                                {content?.markdown ? (
                                    <div
                                        className="prose prose-invert prose-sm max-w-none
                                        prose-headings:text-neutral-200 prose-headings:font-semibold
                                        prose-p:text-neutral-400 prose-p:leading-relaxed
                                        prose-a:text-blue-400 prose-strong:text-neutral-300
                                        prose-table:text-xs prose-td:px-2 prose-td:py-1
                                        prose-th:px-2 prose-th:py-1 prose-th:text-neutral-400
                                        prose-hr:border-neutral-700"
                                    >
                                        <Markdown>{content.markdown}</Markdown>
                                    </div>
                                ) : (
                                    <div className="text-xs text-neutral-600">
                                        No extracted content available
                                    </div>
                                )}
                            </div>
                        </div>
                    </div>
                </div>
            ) : (
                <div className="flex-1 min-w-0 rounded-xl border border-neutral-800 bg-neutral-900/50 flex items-center justify-center text-neutral-600 text-sm">
                    Select a document to view
                </div>
            )}
        </div>
    );
}

function OriginalFilePanel({ docId, name }: { docId: number; name?: string }) {
    const [fileExists, setFileExists] = useState<boolean | null>(null);

    useEffect(() => {
        setFileExists(null);
        fetch(`/api/documents/${docId}/file`, { method: "HEAD" })
            .then((r) => setFileExists(r.ok))
            .catch(() => setFileExists(false));
    }, [docId]);

    if (fileExists === false) return null; // no panel if file missing
    if (fileExists === null)
        return (
            <div className="flex-1 border-r border-neutral-800 flex items-center justify-center text-neutral-600 text-xs">
                Loading...
            </div>
        );

    const isPdf = name?.match(/\.pdf$/i);
    const isImage = name?.match(/\.(png|jpg|jpeg|gif|webp)$/i);

    return (
        <div className="flex-1 border-r border-neutral-800 flex flex-col">
            <div className="px-3 py-2 border-b border-neutral-800/50 text-[10px] font-semibold tracking-widest text-neutral-500">
                ORIGINAL
            </div>
            <div className="flex-1 overflow-hidden">
                {isPdf ? (
                    <iframe
                        src={`/api/documents/${docId}/file#toolbar=1`}
                        className="w-full h-full border-0"
                        title="PDF preview"
                    />
                ) : isImage ? (
                    <div className="p-4 overflow-auto h-full">
                        <img
                            src={`/api/documents/${docId}/file`}
                            alt={name}
                            className="max-w-full rounded"
                        />
                    </div>
                ) : (
                    <iframe
                        src={`/api/documents/${docId}/file`}
                        className="w-full h-full border-0"
                        title="File preview"
                    />
                )}
            </div>
        </div>
    );
}
