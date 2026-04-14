import { useQuery } from "@tanstack/react-query";
import { fetchDocumentDetail, fetchDocumentContent } from "../../api/hermes";
import type { DocumentDetail as DocumentDetailType } from "../../types/hermes";

const PREVIEWABLE = [".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

function getExtension(name: string): string {
    const dot = name.lastIndexOf(".");
    return dot >= 0 ? name.slice(dot).toLowerCase() : "";
}

export function DocumentDetail({
    documentId,
    onBack,
}: {
    documentId: number;
    onBack: () => void;
}) {
    const { data: detail } = useQuery<DocumentDetailType>({
        queryKey: ["document", documentId],
        queryFn: () => fetchDocumentDetail(documentId),
    });
    const { data: content } = useQuery<{ markdown: string }>({
        queryKey: ["document-content", documentId],
        queryFn: () => fetchDocumentContent(documentId),
        enabled: detail?.pipelineStatus.extracted === true,
    });

    if (!detail)
        return <div className="text-neutral-500 text-sm">Loading…</div>;

    const doc = detail.summary;
    const ext = getExtension(doc.originalName);
    const canPreview = PREVIEWABLE.includes(ext);

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
                    {content?.markdown ? (
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
                        {ext === ".pdf" ? (
                            <iframe
                                src={`/api/documents/${documentId}/file`}
                                className="flex-1 rounded-lg border border-neutral-800 bg-white"
                                title="Document preview"
                            />
                        ) : (
                            <img
                                src={`/api/documents/${documentId}/file`}
                                alt={doc.originalName}
                                className="max-w-full rounded-lg border border-neutral-800 object-contain"
                            />
                        )}
                    </div>
                )}
            </div>
        </div>
    );
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
