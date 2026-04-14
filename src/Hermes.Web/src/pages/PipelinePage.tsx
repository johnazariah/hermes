import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { fetchStats } from "../api/hermes";
import type { IndexStats, DocumentSummary } from "../types/hermes";

type Stage =
    | "received"
    | "extracted"
    | "classified"
    | "embedded"
    | "failed"
    | null;

interface StageCounts {
    received: number;
    extracted: number;
    classified: number;
    embedded: number;
    failed: number;
}

interface ActivityEntry {
    id: number;
    level: string;
    category: string;
    message: string;
    timestamp: string;
    documentId: number | null;
}

// ── API helpers ──────────────────────────────────────────────

async function fetchPipelineCounts(): Promise<StageCounts> {
    return (await fetch("/api/pipeline")).json();
}

async function fetchDocsByStage(
    stage: string,
    limit = 50,
): Promise<DocumentSummary[]> {
    const res = await fetch(`/api/documents?stage=${stage}&limit=${limit}`);
    return res.json();
}

async function fetchActivity(limit = 30): Promise<ActivityEntry[]> {
    const res = await fetch(`/api/activity?limit=${limit}`);
    return res.json();
}

async function fetchDocDetail(id: number) {
    const res = await fetch(`/api/documents/${id}`);
    return res.json();
}

// ── Stage badge ──────────────────────────────────────────────

function StageBadge({
    label,
    count,
    icon,
    color,
    active,
    selected,
    onClick,
}: {
    label: string;
    count: number;
    icon: string;
    color: string;
    active: boolean;
    selected: boolean;
    onClick: () => void;
}) {
    return (
        <button
            onClick={onClick}
            className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-left transition-all ${
                selected
                    ? `${color} bg-opacity-10 ring-1 ring-current`
                    : "hover:bg-neutral-800/50"
            }`}
        >
            <span className="text-lg">{icon}</span>
            <div className="flex-1 min-w-0">
                <div className="text-xs text-neutral-400">{label}</div>
                <div className="text-lg font-bold font-mono">
                    {count.toLocaleString()}
                </div>
            </div>
            {active && count > 0 && (
                <span
                    className={`w-2 h-2 rounded-full ${color.replace("text-", "bg-")} animate-pulse`}
                />
            )}
        </button>
    );
}

// ── Journey timeline ─────────────────────────────────────────

function JourneyStep({
    label,
    icon,
    done,
    active,
    detail,
}: {
    label: string;
    icon: string;
    done: boolean;
    active: boolean;
    detail: string;
}) {
    return (
        <div className="flex items-start gap-3 py-2">
            <span className="text-base mt-0.5">
                {done ? "✅" : active ? "🔄" : "⏳"}
            </span>
            <div className="flex-1">
                <div
                    className={`text-sm font-medium ${done ? "text-neutral-300" : active ? "text-blue-400" : "text-neutral-600"}`}
                >
                    {icon} {label}
                </div>
                {detail && (
                    <div className="text-xs text-neutral-500 mt-0.5">
                        {detail}
                    </div>
                )}
            </div>
        </div>
    );
}

function DocumentJourney({ doc }: { doc: any }) {
    if (!doc) return null;
    const s = doc.summary || doc;
    const stage = s.stage || "received";
    const stageOrder = ["received", "extracted", "classified", "embedded"];
    const stageIdx = stageOrder.indexOf(stage);

    return (
        <div className="space-y-1">
            <JourneyStep
                label="Received"
                icon="📨"
                done={stageIdx >= 0}
                active={stage === "received"}
                detail={`source: ${s.sourceType || "unknown"}${s.sender ? `, from: ${s.sender}` : ""}`}
            />
            <JourneyStep
                label="Read"
                icon="📖"
                done={stageIdx >= 1}
                active={stage === "extracted" && stageIdx === 1}
                detail={
                    doc.extractedAt
                        ? `method: ${s.extractionMethod || "auto"}`
                        : ""
                }
            />
            <JourneyStep
                label="Filed"
                icon="🗂️"
                done={stageIdx >= 2}
                active={stage === "classified" && stageIdx === 2}
                detail={
                    s.classificationTier
                        ? `→ ${s.category} (${s.classificationTier}, ${(s.classificationConfidence || 0).toFixed(2)})`
                        : ""
                }
            />
            <JourneyStep
                label="Memorised"
                icon="🧠"
                done={stageIdx >= 3}
                active={stage === "embedded" && stageIdx === 3}
                detail={doc.embeddedAt ? `${doc.chunkCount || 0} chunks` : ""}
            />
            {stage === "failed" && (
                <JourneyStep
                    label="Failed"
                    icon="❌"
                    done={true}
                    active={false}
                    detail={s.error || "Unknown error"}
                />
            )}
        </div>
    );
}

// ── Live feed ────────────────────────────────────────────────

function LiveFeed({ entries }: { entries: ActivityEntry[] }) {
    const levelIcon = (level: string) => {
        switch (level) {
            case "info":
                return "📖";
            case "warn":
                return "⚠️";
            case "error":
                return "❌";
            default:
                return "·";
        }
    };

    return (
        <div className="space-y-1 max-h-64 overflow-y-auto">
            {entries.length === 0 && (
                <div className="text-xs text-neutral-600 py-2">
                    No recent activity
                </div>
            )}
            {entries.map((e) => (
                <div
                    key={e.id}
                    className="text-xs text-neutral-400 flex gap-2 py-0.5"
                >
                    <span>{levelIcon(e.level)}</span>
                    <span className="text-neutral-600 font-mono shrink-0">
                        {new Date(e.timestamp).toLocaleTimeString()}
                    </span>
                    <span className="truncate">{e.message}</span>
                </div>
            ))}
        </div>
    );
}

// ── Progress bars ────────────────────────────────────────────

function MiniProgress({
    done,
    total,
    color,
}: {
    done: number;
    total: number;
    color: string;
}) {
    const pct = total > 0 ? Math.min(done / total, 1) * 100 : 0;
    return (
        <div className="h-1.5 bg-neutral-800 rounded-full overflow-hidden mt-2">
            <div
                className={`h-full rounded-full transition-all duration-1000 ${color}`}
                style={{ width: `${pct}%` }}
            />
        </div>
    );
}

// ── Main Pipeline Page ───────────────────────────────────────

export function PipelinePage() {
    const [selectedStage, setSelectedStage] = useState<Stage>(null);
    const [selectedDocId, setSelectedDocId] = useState<number | null>(null);

    const { data: counts } = useQuery({
        queryKey: ["pipeline-counts"],
        queryFn: fetchPipelineCounts,
        refetchInterval: 5000,
    });

    const { data: stats } = useQuery<IndexStats>({
        queryKey: ["stats"],
        queryFn: fetchStats,
        refetchInterval: 5000,
    });

    const { data: stageDocs } = useQuery({
        queryKey: ["stage-docs", selectedStage],
        queryFn: () => fetchDocsByStage(selectedStage!, 100),
        enabled: selectedStage != null,
        refetchInterval: 10000,
    });

    const { data: docDetail } = useQuery({
        queryKey: ["doc-detail", selectedDocId],
        queryFn: () => fetchDocDetail(selectedDocId!),
        enabled: selectedDocId != null,
    });

    const { data: activity } = useQuery({
        queryKey: ["activity"],
        queryFn: () => fetchActivity(30),
        refetchInterval: 5000,
    });

    const c = counts ?? {
        received: 0,
        extracted: 0,
        classified: 0,
        embedded: 0,
        failed: 0,
    };
    const total = stats?.documentCount ?? 0;
    const read = stats?.extractedCount ?? 0;

    return (
        <div className="flex gap-6 h-[calc(100vh-7rem)]">
            {/* Left: Stages + Feed */}
            <div className="w-64 shrink-0 flex flex-col gap-4">
                {/* Pipeline progress */}
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-4 space-y-1">
                    <div className="flex items-center gap-2 mb-3">
                        <span className="text-sm font-semibold text-neutral-300">
                            PIPELINE
                        </span>
                        {total > 0 && read < total && (
                            <span className="ml-auto text-[10px] text-green-400 animate-pulse">
                                ● ACTIVE
                            </span>
                        )}
                    </div>
                    <MiniProgress
                        done={read}
                        total={total}
                        color="bg-blue-500"
                    />
                    <div className="text-[10px] text-neutral-500 mt-1">
                        {read.toLocaleString()} / {total.toLocaleString()}{" "}
                        processed
                    </div>
                </div>

                {/* Stage buttons */}
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-2 space-y-0.5">
                    <StageBadge
                        label="Received"
                        count={c.received}
                        icon="📨"
                        color="text-green-400"
                        active={c.received > 0}
                        selected={selectedStage === "received"}
                        onClick={() =>
                            setSelectedStage(
                                selectedStage === "received"
                                    ? null
                                    : "received",
                            )
                        }
                    />
                    <StageBadge
                        label="Read"
                        count={c.extracted}
                        icon="📖"
                        color="text-blue-400"
                        active={c.extracted > 0}
                        selected={selectedStage === "extracted"}
                        onClick={() =>
                            setSelectedStage(
                                selectedStage === "extracted"
                                    ? null
                                    : "extracted",
                            )
                        }
                    />
                    <StageBadge
                        label="Filed"
                        count={c.classified}
                        icon="🗂️"
                        color="text-amber-400"
                        active={c.classified > 0}
                        selected={selectedStage === "classified"}
                        onClick={() =>
                            setSelectedStage(
                                selectedStage === "classified"
                                    ? null
                                    : "classified",
                            )
                        }
                    />
                    <StageBadge
                        label="Memorised"
                        count={c.embedded}
                        icon="🧠"
                        color="text-purple-400"
                        active={false}
                        selected={selectedStage === "embedded"}
                        onClick={() =>
                            setSelectedStage(
                                selectedStage === "embedded"
                                    ? null
                                    : "embedded",
                            )
                        }
                    />
                    {c.failed > 0 && (
                        <StageBadge
                            label="Failed"
                            count={c.failed}
                            icon="❌"
                            color="text-red-400"
                            active={false}
                            selected={selectedStage === "failed"}
                            onClick={() =>
                                setSelectedStage(
                                    selectedStage === "failed"
                                        ? null
                                        : "failed",
                                )
                            }
                        />
                    )}
                </div>

                {/* Live feed */}
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-3 flex-1 overflow-hidden flex flex-col">
                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                        LIVE FEED
                    </div>
                    <LiveFeed entries={activity ?? []} />
                </div>
            </div>

            {/* Center: Document list */}
            <div className="flex-1 min-w-0 flex flex-col">
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 flex-1 overflow-hidden flex flex-col">
                    <div className="px-4 py-3 border-b border-neutral-800 flex items-center gap-2">
                        <span className="text-sm font-semibold text-neutral-300">
                            {selectedStage
                                ? `Documents — ${selectedStage}`
                                : "Select a stage to view documents"}
                        </span>
                        {stageDocs && (
                            <span className="text-xs text-neutral-500 ml-auto">
                                {stageDocs.length} shown
                            </span>
                        )}
                    </div>
                    <div className="flex-1 overflow-y-auto">
                        {!selectedStage && (
                            <div className="flex items-center justify-center h-full text-neutral-600 text-sm">
                                Click a pipeline stage to see its documents
                            </div>
                        )}
                        {selectedStage &&
                            stageDocs &&
                            stageDocs.length === 0 && (
                                <div className="flex items-center justify-center h-full text-neutral-600 text-sm">
                                    No documents in this stage
                                </div>
                            )}
                        {stageDocs?.map((doc: DocumentSummary) => (
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
                                    {doc.category && (
                                        <span className="text-amber-400/70">
                                            {doc.category}
                                        </span>
                                    )}
                                    {doc.sender && (
                                        <span className="truncate">
                                            {doc.sender}
                                        </span>
                                    )}
                                    {doc.sourceType && (
                                        <span>{doc.sourceType}</span>
                                    )}
                                </div>
                            </button>
                        ))}
                    </div>
                </div>
            </div>

            {/* Right: Document detail + Journey */}
            <div className="w-80 shrink-0 flex flex-col gap-4">
                {selectedDocId && docDetail ? (
                    <>
                        {/* Journey */}
                        <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-4">
                            <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-3">
                                JOURNEY
                            </div>
                            <DocumentJourney doc={docDetail} />
                        </div>

                        {/* Metadata */}
                        <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-4 flex-1 overflow-y-auto">
                            <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-3">
                                DETAILS
                            </div>
                            <div className="space-y-2 text-xs">
                                <DetailRow
                                    label="Name"
                                    value={docDetail.summary?.originalName}
                                />
                                <DetailRow
                                    label="Category"
                                    value={docDetail.summary?.category}
                                />
                                <DetailRow
                                    label="Source"
                                    value={docDetail.summary?.sourceType}
                                />
                                <DetailRow
                                    label="Sender"
                                    value={docDetail.summary?.sender}
                                />
                                <DetailRow
                                    label="Account"
                                    value={docDetail.summary?.account}
                                />
                                <DetailRow
                                    label="Vendor"
                                    value={
                                        docDetail.summary?.vendor ||
                                        docDetail.summary?.extractedVendor
                                    }
                                />
                                <DetailRow
                                    label="Amount"
                                    value={docDetail.summary?.extractedAmount?.toFixed(
                                        2,
                                    )}
                                />
                                <DetailRow
                                    label="Date"
                                    value={docDetail.summary?.extractedDate}
                                />
                                <DetailRow
                                    label="Confidence"
                                    value={docDetail.summary?.classificationConfidence?.toFixed(
                                        2,
                                    )}
                                />
                                <DetailRow
                                    label="Tier"
                                    value={
                                        docDetail.summary?.classificationTier
                                    }
                                />
                            </div>
                            {docDetail.extractedText && (
                                <div className="mt-4 pt-4 border-t border-neutral-800">
                                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                                        PREVIEW
                                    </div>
                                    <div className="text-xs text-neutral-400 whitespace-pre-wrap max-h-48 overflow-y-auto">
                                        {docDetail.extractedText.substring(
                                            0,
                                            500,
                                        )}
                                        {docDetail.extractedText.length > 500 &&
                                            "..."}
                                    </div>
                                </div>
                            )}
                        </div>
                    </>
                ) : (
                    <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-4 flex-1 flex items-center justify-center text-neutral-600 text-sm">
                        Select a document to see its journey
                    </div>
                )}
            </div>
        </div>
    );
}

function DetailRow({
    label,
    value,
}: {
    label: string;
    value: string | undefined | null;
}) {
    if (!value) return null;
    return (
        <div className="flex gap-2">
            <span className="text-neutral-600 shrink-0 w-20">{label}</span>
            <span className="text-neutral-300 truncate">{value}</span>
        </div>
    );
}
