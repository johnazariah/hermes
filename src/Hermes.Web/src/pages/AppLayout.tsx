import { NavLink, Outlet } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { fetchStats } from "../api/hermes";
import type { IndexStats } from "../types/hermes";

function NavTab({
    to,
    label,
    badge,
}: {
    to: string;
    label: string;
    badge?: number;
}) {
    return (
        <NavLink
            to={to}
            className={({ isActive }) =>
                `px-4 py-2 text-sm font-medium rounded-lg transition-colors ${
                    isActive
                        ? "bg-neutral-800 text-white"
                        : "text-neutral-400 hover:text-white hover:bg-neutral-800/50"
                }`
            }
        >
            <span className="flex items-center gap-2">
                {label}
                {badge != null && badge > 0 && (
                    <span className="inline-flex items-center justify-center w-5 h-5 text-[10px] font-bold bg-blue-500 text-white rounded-full animate-pulse">
                        {badge > 99 ? "99+" : badge}
                    </span>
                )}
            </span>
        </NavLink>
    );
}

export function AppLayout() {
    const { data: stats } = useQuery<IndexStats>({
        queryKey: ["stats"],
        queryFn: fetchStats,
        refetchInterval: 5000,
    });

    const total = stats?.documentCount ?? 0;
    const embedded = stats?.embeddedCount ?? 0;
    const isActive = total > 0 && embedded < total;
    const pending = total - embedded;

    return (
        <div className="min-h-screen bg-neutral-950 text-neutral-100 flex flex-col">
            {/* Top navigation bar */}
            <header className="border-b border-neutral-800 bg-neutral-900/80 backdrop-blur-sm sticky top-0 z-50">
                <div className="max-w-[1600px] mx-auto px-4 h-12 flex items-center gap-1">
                    <NavLink to="/" className="flex items-center gap-2 mr-6">
                        <span className="text-lg">⚡</span>
                        <span className="font-bold text-sm tracking-wide">
                            HERMES
                        </span>
                    </NavLink>

                    <nav className="flex items-center gap-1">
                        <NavTab
                            to="/"
                            label="Pipeline"
                            badge={isActive ? pending : undefined}
                        />
                        <NavTab to="/documents" label="Documents" />
                        <NavTab to="/search" label="Search" />
                        <NavTab to="/chat" label="Chat" />
                        <NavTab to="/settings" label="Settings" />
                    </nav>

                    {stats && (
                        <div className="ml-auto flex items-center gap-4 text-xs text-neutral-500">
                            <span>{total.toLocaleString()} docs</span>
                            <span>
                                DB: {stats.databaseSizeMb.toFixed(1)} MB
                            </span>
                            {isActive && (
                                <span className="text-green-400 animate-pulse">
                                    ● Processing
                                </span>
                            )}
                        </div>
                    )}
                </div>
            </header>

            {/* Page content */}
            <main className="flex-1 max-w-[1600px] mx-auto w-full px-4 py-6">
                <Outlet />
            </main>
        </div>
    );
}
