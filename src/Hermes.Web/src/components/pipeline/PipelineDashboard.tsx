import { useQuery } from "@tanstack/react-query";
import { fetchStats } from "../../api/hermes";

interface PipelineStatus {
    inbox: number;
    reading: number;
    filing: number;
    failed: number;
    received: number;
    read: number;
    memorised: number;
    emailsQueued: number;
    emailsProcessed: number;
}

async function fetchPipeline(): Promise<PipelineStatus> {
    return (await fetch("/api/pipeline")).json();
}

function ProgressBar({
    label,
    done,
    total,
    color,
    icon,
    tooltip,
}: {
    label: string;
    done: number;
    total: number;
    color: string;
    icon: string;
    tooltip: string;
}) {
    const fill = total > 0 ? Math.min(done / total, 1) * 100 : 0;
    const active = done < total && total > 0;

    return (
        <div className="flex items-center gap-3" title={tooltip}>
            <span className="text-lg w-8 text-center">{icon}</span>
            <div className="flex-1">
                <div className="flex justify-between text-xs mb-1">
                    <span className="text-neutral-300">{label}</span>
                    <span
                        className={`font-mono ${active ? "text-white" : "text-neutral-500"}`}
                    >
                        {done.toLocaleString()} / {total.toLocaleString()}
                    </span>
                </div>
                <div className="h-2 bg-neutral-800 rounded-full overflow-hidden">
                    <div
                        className={`h-full rounded-full transition-all duration-1000 ease-out ${color}`}
                        style={{ width: `${fill}%` }}
                    />
                </div>
            </div>
        </div>
    );
}

function FlowArrow({ active }: { active: boolean }) {
    return (
        <div className="flex justify-center py-1">
            <span
                className={`text-xs transition-opacity duration-500 ${active ? "text-blue-400 animate-pulse" : "text-neutral-700"}`}
            >
                ↓
            </span>
        </div>
    );
}

function StatCard({
    label,
    value,
    icon,
    highlight,
}: {
    label: string;
    value: string;
    icon: string;
    highlight?: boolean;
}) {
    return (
        <div
            className={`rounded-lg border px-4 py-3 ${highlight ? "border-blue-500/30 bg-blue-500/5" : "border-neutral-800 bg-neutral-900"}`}
        >
            <div className="text-[10px] text-neutral-500 uppercase tracking-wider mb-1">
                {icon} {label}
            </div>
            <div className="text-xl font-bold font-mono">{value}</div>
        </div>
    );
}

export function PipelineDashboard() {
    const { data: pipeline } = useQuery({
        queryKey: ["pipeline"],
        queryFn: fetchPipeline,
        refetchInterval: 5000,
    });

    const { data: stats } = useQuery({
        queryKey: ["stats"],
        queryFn: fetchStats,
        refetchInterval: 10000,
    });

    const p = pipeline ?? {
        inbox: 0,
        reading: 0,
        filing: 0,
        failed: 0,
        received: 0,
        read: 0,
        memorised: 0,
        emailsQueued: 0,
        emailsProcessed: 0,
    };

    const total = p.received;
    const awaitingReading = total - p.read;
    const awaitingMemorising = p.read - p.memorised;
    const anyActive = total > 0 && (awaitingReading > 0 || awaitingMemorising > 0 || p.emailsQueued > p.emailsProcessed);
    const emailsPending = p.emailsQueued - p.emailsProcessed;

    return (
        <div className="space-y-6">
            {/* Flow diagram */}
            <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-5">
                <div className="flex items-center gap-2 mb-4">
                    <span className="text-lg">⚡</span>
                    <span className="text-sm font-semibold tracking-wide text-neutral-300">
                        PIPELINE
                    </span>
                    {anyActive && (
                        <span className="ml-auto text-[10px] text-green-400 animate-pulse">
                            ● ACTIVE
                        </span>
                    )}
                    {!anyActive && p.received > 0 && (
                        <span className="ml-auto text-[10px] text-neutral-500">
                            ● IDLE
                        </span>
                    )}
                </div>

                <div className="space-y-1">
                    {p.emailsQueued > 0 && (
                        <>
                            <ProgressBar
                                label="Downloading emails"
                                done={p.emailsProcessed}
                                total={p.emailsQueued}
                                color="bg-green-500"
                                icon="📧"
                                tooltip="Fetching emails from Gmail — each email is checked for attachments and saved locally"
                            />
                            <FlowArrow active={emailsPending > 0} />
                        </>
                    )}
                    <ProgressBar
                        label="Reading documents"
                        done={p.read}
                        total={total}
                        color="bg-blue-500"
                        icon="📖"
                        tooltip="Extracting text from PDFs, emails, spreadsheets, and other documents so they can be searched"
                    />
                    <FlowArrow active={awaitingReading > 0} />
                    <ProgressBar
                        label="Memorising"
                        done={p.memorised}
                        total={p.read}
                        color="bg-purple-500"
                        icon="🧠"
                        tooltip="Creating searchable memory — documents are indexed so you can find them by asking questions"
                    />
                    {p.failed > 0 && (
                        <>
                            <FlowArrow active={true} />
                            <ProgressBar
                                label="Failed"
                                done={p.failed}
                                total={p.failed}
                                color="bg-red-500"
                                icon="❌"
                                tooltip="Documents that couldn't be processed — check the dead letter panel for details"
                            />
                        </>
                    )}
                </div>
            </div>

            {/* Summary cards */}
            <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
                <StatCard
                    icon="📨"
                    label="Received"
                    value={p.received.toLocaleString()}
                />
                <StatCard
                    icon="📖"
                    label="Read"
                    value={p.read.toLocaleString()}
                    highlight={p.read < p.received}
                />
                <StatCard
                    icon="🧠"
                    label="Memorised"
                    value={p.memorised.toLocaleString()}
                    highlight={p.memorised < p.read}
                />
                <StatCard
                    icon="💾"
                    label="Database"
                    value={
                        stats ? `${stats.databaseSizeMb.toFixed(1)} MB` : "—"
                    }
                />
            </div>
        </div>
    );
}
