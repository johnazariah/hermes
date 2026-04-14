import { useQuery } from "@tanstack/react-query";
import { fetchStats, fetchCategories, triggerSync } from "../../api/hermes";
import type { IndexStats, CategoryCount } from "../../types/hermes";

function StageRow({
    label,
    value,
    total,
}: {
    label: string;
    value: number;
    total: number;
}) {
    const pct = total > 0 ? (value / total) * 100 : 0;
    return (
        <div className="space-y-1">
            <div className="flex justify-between text-xs text-neutral-400">
                <span>{label}</span>
                <span>
                    {value.toLocaleString()} / {total.toLocaleString()}
                </span>
            </div>
            <div className="h-1.5 bg-neutral-700 rounded-full overflow-hidden">
                <div
                    className="h-full bg-blue-500 rounded-full transition-all"
                    style={{ width: `${pct}%` }}
                />
            </div>
        </div>
    );
}

type ViewType =
    | { kind: "category"; value: string }
    | { kind: "smart"; value: string }
    | null;

export function Sidebar({
    onSelectView,
    selectedView,
    onOpenSettings,
}: {
    onSelectView: (view: ViewType) => void;
    selectedView: ViewType;
    onOpenSettings: () => void;
}) {
    const { data: stats } = useQuery<IndexStats>({
        queryKey: ["stats"],
        queryFn: fetchStats,
        refetchInterval: 5000,
    });
    const { data: categories } = useQuery<CategoryCount[]>({
        queryKey: ["categories"],
        queryFn: fetchCategories,
        refetchInterval: 10000,
    });
    const { data: tags } = useQuery<{ tag: string; count: number }[]>({
        queryKey: ["tags"],
        queryFn: () => fetch("/api/tags").then((r) => r.json()),
        refetchInterval: 30000,
    });

    const total = stats?.documentCount ?? 0;
    const extracted = stats?.extractedCount ?? 0;
    const understood = stats?.understoodCount ?? 0;
    const embedded = stats?.embeddedCount ?? 0;
    const awaitExtract = stats?.awaitingExtract ?? 0;
    const awaitUnderstand = stats?.awaitingUnderstand ?? 0;
    const awaitEmbed = stats?.awaitingEmbed ?? 0;
    const unsorted =
        categories?.find((c) => c.category === "unsorted")?.count ?? 0;

    const isSelected = (kind: string, value: string) =>
        selectedView?.kind === kind && selectedView?.value === value;

    return (
        <aside className="w-64 bg-neutral-900 border-r border-neutral-800 flex flex-col h-full">
            {/* Header */}
            <div className="px-4 py-3 border-b border-neutral-800 flex items-center gap-2 shrink-0">
                <button
                    onClick={() => onSelectView(null)}
                    className="text-lg hover:opacity-70"
                    title="Home"
                >
                    ⚡
                </button>
                <button
                    onClick={() => onSelectView(null)}
                    className="text-sm font-bold tracking-widest text-neutral-200 hover:text-white"
                >
                    HERMES
                </button>
                <button
                    onClick={() => triggerSync()}
                    className="ml-auto text-neutral-500 hover:text-neutral-200 text-sm"
                    title="Sync Now"
                >
                    ⟳
                </button>
                <button
                    onClick={onOpenSettings}
                    className="text-neutral-500 hover:text-neutral-200 text-sm"
                    title="Settings"
                >
                    ⚙
                </button>
            </div>

            {/* Scrollable content */}
            <div className="flex-1 overflow-y-auto">
                {/* Pipeline progress */}
                <div className="px-4 py-3 space-y-3 border-b border-neutral-800">
                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500">
                        PIPELINE
                    </div>
                    <StageRow label="📖 Read" value={extracted} total={total} />
                    <StageRow
                        label="� Understood"
                        value={understood}
                        total={extracted}
                    />
                    <StageRow
                        label="🧠 Memorised"
                        value={embedded}
                        total={extracted}
                    />
                    {(awaitExtract > 0 ||
                        awaitUnderstand > 0 ||
                        awaitEmbed > 0) && (
                        <div className="text-[10px] text-blue-400 space-y-0.5">
                            {awaitExtract > 0 && (
                                <div>
                                    ⏳ {awaitExtract.toLocaleString()} awaiting
                                    reading
                                </div>
                            )}
                            {awaitUnderstand > 0 && (
                                <div>
                                    ⏳ {awaitUnderstand.toLocaleString()}{" "}
                                    awaiting understanding
                                </div>
                            )}
                            {awaitEmbed > 0 && (
                                <div>
                                    ⏳ {awaitEmbed.toLocaleString()} awaiting
                                    memorising
                                </div>
                            )}
                        </div>
                    )}
                    {stats && (
                        <div className="text-[10px] text-neutral-600">
                            DB: {stats.databaseSizeMb.toFixed(1)} MB
                        </div>
                    )}
                </div>

                {/* Smart Views */}
                <div className="px-4 py-3 border-b border-neutral-800">
                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                        SMART VIEWS
                    </div>
                    <div className="space-y-0.5">
                        {unsorted > 0 && (
                            <SidebarItem
                                icon="🔴"
                                label="Needs Review"
                                count={unsorted}
                                selected={isSelected("smart", "review")}
                                onClick={() =>
                                    onSelectView({
                                        kind: "smart",
                                        value: "review",
                                    })
                                }
                            />
                        )}
                        <SidebarItem
                            icon="⭐"
                            label="Starred"
                            selected={isSelected("smart", "starred")}
                            onClick={() =>
                                onSelectView({
                                    kind: "smart",
                                    value: "starred",
                                })
                            }
                        />
                        <SidebarItem
                            icon="📅"
                            label="Recent"
                            selected={isSelected("smart", "recent")}
                            onClick={() =>
                                onSelectView({ kind: "smart", value: "recent" })
                            }
                        />
                        <SidebarItem
                            icon="📋"
                            label="Activity"
                            selected={isSelected("smart", "activity")}
                            onClick={() =>
                                onSelectView({
                                    kind: "smart",
                                    value: "activity",
                                })
                            }
                        />
                    </div>
                </div>

                {/* Categories */}
                <div className="px-4 py-3 flex-1">
                    <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                        CATEGORIES{" "}
                        {total > 0 && (
                            <span className="text-neutral-600">
                                ({total.toLocaleString()})
                            </span>
                        )}
                    </div>
                    {categories && categories.length > 0 ? (
                        <div className="space-y-0.5">
                            {categories.map((cat) => (
                                <SidebarItem
                                    key={cat.category}
                                    icon="📁"
                                    label={cat.category}
                                    count={cat.count}
                                    selected={isSelected(
                                        "category",
                                        cat.category,
                                    )}
                                    onClick={() =>
                                        onSelectView({
                                            kind: "category",
                                            value: cat.category,
                                        })
                                    }
                                />
                            ))}
                        </div>
                    ) : (
                        <div className="text-xs text-neutral-600">
                            No documents yet.
                        </div>
                    )}
                </div>

                {/* Tags */}
                {tags && tags.length > 0 && (
                    <div className="px-4 py-3 border-t border-neutral-800">
                        <div className="text-[10px] font-semibold tracking-widest text-neutral-500 mb-2">
                            TAGS
                        </div>
                        <div className="space-y-0.5">
                            {tags.slice(0, 10).map((t) => (
                                <SidebarItem
                                    key={t.tag}
                                    icon="🏷"
                                    label={t.tag}
                                    count={t.count}
                                    selected={false}
                                    onClick={() => {}}
                                />
                            ))}
                        </div>
                    </div>
                )}

                {/* Footer */}
            </div>
            <div className="px-4 py-2 border-t border-neutral-800 shrink-0">
                <button
                    onClick={() => onSelectView(null)}
                    className="text-xs text-neutral-500 hover:text-neutral-300"
                >
                    ← Home
                </button>
            </div>
        </aside>
    );
}

function SidebarItem({
    icon,
    label,
    count,
    selected,
    onClick,
}: {
    icon: string;
    label: string;
    count?: number;
    selected: boolean;
    onClick: () => void;
}) {
    return (
        <button
            onClick={onClick}
            className={`w-full text-left px-2 py-1.5 rounded text-sm flex items-center gap-2 transition-colors ${
                selected
                    ? "bg-neutral-800 text-neutral-100"
                    : "hover:bg-neutral-800/50 text-neutral-400"
            }`}
        >
            <span className="text-xs">{icon}</span>
            <span className="flex-1 truncate">{label}</span>
            {count != null && (
                <span className="text-xs text-neutral-600">({count})</span>
            )}
        </button>
    );
}

export type { ViewType };
