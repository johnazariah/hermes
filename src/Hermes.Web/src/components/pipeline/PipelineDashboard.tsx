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

function ChannelBar({
    label,
    depth,
    maxDepth,
    color,
    icon,
    tooltip,
}: {
    label: string;
    depth: number;
    maxDepth: number;
    color: string;
    icon: string;
    tooltip: string;
}) {
    const fill = maxDepth > 0 ? Math.min(depth / maxDepth, 1) * 100 : 0;
    const isActive = depth > 0;

    return (
        <div className="flex items-center gap-3 group relative" title={tooltip}>
            <span className="text-lg w-8 text-center">{icon}</span>
            <div className="flex-1">
                <div className="flex justify-between text-xs mb-1">
                    <span className="text-neutral-300">{label}</span>
                    <span
                        className={`font-mono ${isActive ? "text-white" : "text-neutral-600"}`}
                    >
                        {depth.toLocaleString()}
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

    const maxChannel = Math.max(p.inbox, p.reading, p.filing, 1);
    const anyActive = p.inbox > 0 || p.reading > 0 || p.filing > 0;
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
                            <ChannelBar
                                label="Emails"
                                depth={emailsPending > 0 ? emailsPending : 0}
                                maxDepth={Math.max(p.emailsQueued, 1)}
                                color="bg-green-500"
                                icon="📧"
                            />
                            <div className="text-[10px] text-neutral-500 text-right">
                                {p.emailsProcessed.toLocaleString()} /{" "}
                                {p.emailsQueued.toLocaleString()} fetched
                            </div>
                            <FlowArrow active={emailsPending > 0} />
                        </>
                    )}
                    <ChannelBar
                        label="Inbox"
                        depth={p.inbox}
                        maxDepth={maxChannel}
                        color="bg-amber-500"
                        icon="📬"
                    />
                    <FlowArrow active={p.inbox > 0} />
                    <ChannelBar
                        label="Reading"
                        depth={p.reading}
                        maxDepth={maxChannel}
                        color="bg-blue-500"
                        icon="📖"
                    />
                    <FlowArrow active={p.reading > 0} />
                    <ChannelBar
                        label="Filing"
                        depth={p.filing}
                        maxDepth={maxChannel}
                        color="bg-purple-500"
                        icon="🗂️"
                    />
                    {p.failed > 0 && (
                        <>
                            <FlowArrow active={true} />
                            <ChannelBar
                                label="Failed"
                                depth={p.failed}
                                maxDepth={maxChannel}
                                color="bg-red-500"
                                icon="❌"
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
