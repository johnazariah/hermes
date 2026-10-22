import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { fetchDocumentDetail, fetchDocumentContent } from "../../api/hermes";
import { access } from "../../api/http";
import { loadDocumentPreview, previewKind, type PreviewSource } from "../../api/preview";
import { useAccess } from "../../hooks/useAccess";
import type { DocumentDetail as DocumentDetailType } from "../../types/hermes";

export function DocumentDetail({
    documentId,
    onBack,
}: {
    documentId: number;
    onBack: () => void;
}) {
    const authority = useAccess();
    const { data: detail, error, isPending } = useQuery<DocumentDetailType>({
        queryKey: ["document", authority.instanceId, documentId],
        queryFn: ({ signal }) => fetchDocumentDetail(documentId, signal),
        enabled: Boolean(authority.instanceId),
    });
    const { data: content, error: contentError, isPending: contentPending } = useQuery<{ markdown: string }>({
        queryKey: ["document-content", authority.instanceId, documentId],
        queryFn: ({ signal }) => fetchDocumentContent(documentId, signal),
        enabled: detail?.pipelineStatus.extracted === true,
    });

    if (error) return <div role="alert">
        <button onClick={onBack}>Back to documents</button>
        <p>Unable to load document: {error.message}</p>
    </div>;
    if (isPending || !detail)
        return <div className="text-neutral-500 text-sm">Loading…</div>;

    const doc = detail.summary;
    const canPreview = previewKind(doc.originalName) !== undefined;

    return (
        <div className="h-full flex flex-col">
            <button
                onClick={onBack}
                className="text-xs text-neutral-500 hover:text-neutral-300 mb-3 self-start"
            >
                ← Back
            </button>

            <h2 className="text-lg font-semibold mb-1">{doc.originalName}</h2>

            {/* Metadata row */}
            <div className="flex flex-wrap gap-x-6 gap-y-1 text-sm mb-3 text-neutral-400">
                {doc.sender && <span>📧 {doc.sender}</span>}
                {doc.extractedDate && <span>📅 {doc.extractedDate}</span>}
                {doc.extractedAmount != null && (
                    <span>💰 ${doc.extractedAmount.toFixed(2)}</span>
                )}
                {detail.vendor && <span>🏢 {detail.vendor}</span>}
            </div>

            {/* Pipeline status */}
            <div className="flex gap-2 mb-4">
                <StatusBadge
                    label="Understood"
                    done={detail.pipelineStatus.understood}
                />
                <StatusBadge
                    label="Extracted"
                    done={detail.pipelineStatus.extracted}
                />
                <StatusBadge
                    label="Embedded"
                    done={detail.pipelineStatus.embedded}
                />
            </div>

            {/* Split view: extracted markdown + original document */}
            <div
                className={`flex-1 min-h-0 ${canPreview ? "grid grid-cols-2 gap-4" : ""}`}
            >
                {/* Left: Extracted content */}
                <div className="flex flex-col min-h-0">
                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                        EXTRACTED
                    </div>
                    {contentError ? <p role="alert">Unable to load content: {contentError.message}</p>
                    : detail.pipelineStatus.extracted && contentPending ? <p>Loading content...</p>
                    : content?.markdown ? (
                        <div className="bg-neutral-900 border border-neutral-800 rounded-lg p-4 text-sm whitespace-pre-wrap font-mono leading-relaxed overflow-y-auto flex-1">
                            {content.markdown}
                        </div>
                    ) : detail.pipelineStatus.extracted ? (
                        <div className="text-sm text-neutral-500">
                            No content available.
                        </div>
                    ) : (
                        <div className="text-sm text-neutral-500">
                            Awaiting extraction…
                        </div>
                    )}
                </div>

                {/* Right: Original document preview */}
                {canPreview && (
                    <div className="flex flex-col min-h-0">
                        <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                            ORIGINAL
                        </div>
                        {authority.instanceId && <OriginalPreview
                            key={JSON.stringify([authority.instanceId, authority.generation, documentId, detail.filePath, doc.originalName])}
                            source={{ documentId, originalName: doc.originalName, filePath: detail.filePath,
                                instanceId: authority.instanceId, generation: authority.generation }}
                        />}
                    </div>
                )}
            </div>
        </div>
    );
}

function OriginalPreview({ source }: { source: PreviewSource }) {
    const [preview, setPreview] = useState<{ url?: string; dispose?: () => void; error?: string }>({});
    const { documentId, originalName, filePath, instanceId, generation } = source;
    useEffect(() => {
        const controller = new AbortController();
        let dispose: (() => void) | undefined;
        void loadDocumentPreview(access, { documentId, originalName, filePath, instanceId, generation }, controller.signal)
            .then(resource => {
                dispose = resource.dispose;
                if (controller.signal.aborted) resource.dispose();
                else setPreview({ url: resource.url, dispose: resource.dispose });
            })
            .catch(error => {
                if (!controller.signal.aborted) setPreview({ error: error instanceof Error ? error.message : "Preview failed." });
            });
        return () => { controller.abort(); dispose?.(); };
    }, [documentId, originalName, filePath, instanceId, generation]);
    if (preview.error) return <p role="alert">Unable to preview original: {preview.error}</p>;
    if (!preview.url) return <p role="status">Loading original…</p>;
    const failed = () => {
        preview.dispose?.();
        setPreview({ error: "The browser could not display the original file." });
    };
    return previewKind(originalName) === "pdf"
        ? <div className="flex-1 min-h-0 flex flex-col gap-2">
            {/* A sandboxed frame blocks Chromium's PDF plugin, even for a verified PDF blob. */}
            <object data={preview.url} type="application/pdf" aria-label="Document preview"
                className="flex-1 w-full min-h-0 rounded-lg border border-neutral-800 bg-white">
                <p>Inline PDF preview is unavailable in this browser.</p>
            </object>
            <p className="text-xs text-neutral-400">
                If the PDF is blank or unavailable,{" "}
                <a href={preview.url} download={originalName} className="underline">Download original PDF</a>.
            </p>
        </div>
        : <img src={preview.url} alt={originalName} onError={failed}
            className="max-w-full rounded-lg border border-neutral-800 object-contain" />;
}

function StatusBadge({ label, done }: { label: string; done: boolean }) {
    return (
        <span
            className={`text-xs px-2 py-0.5 rounded ${done ? "bg-green-900/50 text-green-400" : "bg-neutral-800 text-neutral-500"}`}
        >
            {done ? "✓" : "○"} {label}
        </span>
    );
}
