import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { fetchStats } from "../../api/hermes";
import { useTheme } from "../../hooks/useTheme";
import { CommandPalette } from "../CommandPalette";
import type { IndexStats } from "../../types/hermes";

interface NavItemProps {
    to: string;
    icon: string;
    label: string;
    badge?: number;
}

function NavItem({ to, icon, label, badge }: NavItemProps) {
    return (
        <NavLink
            to={to}
            end={to === "/"}
            className={({ isActive }) =>
                `flex items-center gap-3 px-3 py-2 text-sm font-medium rounded-lg transition-colors ${
                    isActive
                        ? "bg-neutral-200 text-black dark:bg-neutral-800 dark:text-white"
                        : "text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800/50 hover:text-black dark:hover:text-white"
                }`
            }
        >
            <span className="text-base">{icon}</span>
            <span>{label}</span>
            {badge != null && badge > 0 && (
                <span className="ml-auto inline-flex items-center justify-center min-w-5 h-5 px-1.5 text-[10px] font-bold bg-amber-500 text-white rounded-full">
                    {badge > 99 ? "99+" : badge}
                </span>
            )}
        </NavLink>
    );
}

export function AppShell() {
    const { theme, toggleTheme } = useTheme();
    const navigate = useNavigate();

    const { data: stats } = useQuery<IndexStats>({
        queryKey: ["stats"],
        queryFn: fetchStats,
        refetchInterval: 5000,
    });

    const { data: suggestions } = useQuery<unknown[]>({
        queryKey: ["suggestion-count"],
        queryFn: async () => {
            const res = await fetch("/api/suggestions");
            if (!res.ok) return [];
            return res.json();
        },
        refetchInterval: 30000,
    });

    // First-run redirect: if no accounts and no preferences, send to onboarding
    const { data: accounts } = useQuery<{ account: string }[]>({
        queryKey: ["sync-accounts-onboard"],
        queryFn: async () => {
            const res = await fetch("/api/sync/accounts");
            if (!res.ok) return [];
            return res.json();
        },
        staleTime: Infinity,
    });

    const { data: preferences } = useQuery<string>({
        queryKey: ["preferences-onboard"],
        queryFn: async () => {
            const res = await fetch("/api/preferences");
            if (!res.ok) return "";
            return res.text();
        },
        staleTime: Infinity,
    });

    useEffect(() => {
        if (accounts === undefined || preferences === undefined) return;
        const alreadyOnboarded = localStorage.getItem("hermes-onboarded");
        if (alreadyOnboarded) return;

        const hasAccounts = accounts.length > 0;
        const hasPreferences = (preferences ?? "").trim().length > 0;
        if (!hasAccounts && !hasPreferences) {
            navigate("/onboarding", { replace: true });
        } else {
            localStorage.setItem("hermes-onboarded", "true");
        }
    }, [accounts, preferences, navigate]);

    const pendingSuggestions = suggestions?.length ?? 0;
    const total = stats?.documentCount ?? 0;
    const dbSize = stats?.databaseSizeMb ?? 0;

    return (
        <div className="min-h-screen flex bg-white text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
            {/* Sidebar */}
            <aside className="fixed inset-y-0 left-0 w-56 flex flex-col border-r border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 z-40">
                {/* Logo */}
                <div className="h-14 flex items-center gap-2 px-4 border-b border-neutral-200 dark:border-neutral-800">
                    <span className="text-lg">⚡</span>
                    <span className="font-bold text-sm tracking-wide">HERMES</span>
                </div>

                {/* Navigation */}
                <nav className="flex-1 px-3 py-4 space-y-1">
                    <NavItem
                        to="/"
                        icon="🏠"
                        label="Home"
                        badge={pendingSuggestions}
                    />
                    <NavItem to="/documents" icon="📄" label="Documents" />
                    <NavItem to="/search" icon="🔍" label="Search" />
                    <NavItem to="/settings" icon="⚙️" label="Settings" />

                    {/* Cmd+K hint */}
                    <div className="pt-4">
                        <button
                            onClick={() => {
                                document.dispatchEvent(
                                    new KeyboardEvent("keydown", {
                                        key: "k",
                                        metaKey: true,
                                    })
                                );
                            }}
                            className="w-full flex items-center gap-2 px-3 py-2 text-xs text-neutral-500 rounded-lg border border-neutral-200 dark:border-neutral-800 hover:border-neutral-400 dark:hover:border-neutral-600 transition-colors cursor-pointer"
                        >
                            <span>🔎</span>
                            <span>Search…</span>
                            <kbd className="ml-auto text-[10px] bg-neutral-100 dark:bg-neutral-800 px-1.5 py-0.5 rounded">
                                ⌘K
                            </kbd>
                        </button>
                    </div>
                </nav>

                {/* Footer: stats + theme toggle */}
                <div className="px-3 py-3 border-t border-neutral-200 dark:border-neutral-800 space-y-2">
                    {/* Stats */}
                    <div className="text-[11px] text-neutral-500 space-y-1 px-1">
                        <div className="flex justify-between">
                            <span>Documents</span>
                            <span>{total.toLocaleString()}</span>
                        </div>
                        <div className="flex justify-between">
                            <span>DB size</span>
                            <span>{dbSize.toFixed(1)} MB</span>
                        </div>
                    </div>

                    {/* Theme toggle */}
                    <button
                        onClick={toggleTheme}
                        className="w-full flex items-center gap-2 px-3 py-2 text-sm text-neutral-600 dark:text-neutral-400 rounded-lg hover:bg-neutral-100 dark:hover:bg-neutral-800/50 transition-colors cursor-pointer"
                    >
                        <span>{theme === "dark" ? "☀️" : "🌙"}</span>
                        <span>{theme === "dark" ? "Light mode" : "Dark mode"}</span>
                    </button>
                </div>
            </aside>

            {/* Main content */}
            <main className="ml-56 flex-1 min-h-screen">
                <div className="max-w-[1400px] mx-auto px-6 py-6">
                    <Outlet />
                </div>
            </main>

            {/* Command palette */}
            <CommandPalette />
        </div>
    );
}
