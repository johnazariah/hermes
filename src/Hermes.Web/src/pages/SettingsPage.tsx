import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

// ── Types ────────────────────────────────────────────────────

interface SyncAccount {
    account: string;
    lastSyncAt: string | null;
    messageCount: number;
}

interface LearnedPattern {
    senderDomain: string;
    documentType: string;
    count: number;
    avgConfidence: number;
    lastSeen: string | null;
}

// ── API helpers ──────────────────────────────────────────────

async function fetchConfig(): Promise<string> {
    const res = await fetch("/api/settings");
    return res.text();
}

async function saveConfig(yaml: string): Promise<void> {
    await fetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "text/yaml" },
        body: yaml,
    });
}

async function fetchSyncAccounts(): Promise<SyncAccount[]> {
    const res = await fetch("/api/sync/accounts");
    return res.json();
}

async function fetchPreferences(): Promise<string> {
    const res = await fetch("/api/preferences");
    return res.text();
}

async function savePreferences(text: string): Promise<void> {
    await fetch("/api/preferences", {
        method: "PUT",
        headers: { "Content-Type": "text/plain" },
        body: text,
    });
}

async function fetchLearnedPatterns(): Promise<LearnedPattern[]> {
    const res = await fetch("/api/learned-patterns");
    return res.json();
}

// ── Collapsible section ──────────────────────────────────────

function Section({
    title,
    subtitle,
    defaultOpen = true,
    actions,
    children,
}: {
    title: string;
    subtitle?: string;
    defaultOpen?: boolean;
    actions?: React.ReactNode;
    children: React.ReactNode;
}) {
    const [open, setOpen] = useState(defaultOpen);

    return (
        <div className="rounded-xl border border-neutral-200 bg-white p-5 dark:border-neutral-800 dark:bg-neutral-900/50">
            <div className="flex items-center justify-between">
                <button
                    type="button"
                    onClick={() => setOpen(!open)}
                    className="flex items-center gap-2 text-left"
                >
                    <span className="text-xs text-neutral-400 dark:text-neutral-500">
                        {open ? "▼" : "▶"}
                    </span>
                    <div>
                        <h2 className="text-sm font-semibold text-neutral-900 dark:text-neutral-100">
                            {title}
                        </h2>
                        {subtitle && (
                            <p className="mt-0.5 text-xs text-neutral-500 dark:text-neutral-500">
                                {subtitle}
                            </p>
                        )}
                    </div>
                </button>
                {actions && <div className="flex items-center gap-2">{actions}</div>}
            </div>
            {open && <div className="mt-4">{children}</div>}
        </div>
    );
}

// ── Settings page ────────────────────────────────────────────

export function SettingsPage() {
    const [configText, setConfigText] = useState<string | null>(null);
    const [prefsText, setPrefsText] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [saved, setSaved] = useState(false);
    const [prefsSaved, setPrefsSaved] = useState(false);
    const queryClient = useQueryClient();

    const { data: config } = useQuery({
        queryKey: ["config"],
        queryFn: fetchConfig,
    });

    const { data: accounts } = useQuery({
        queryKey: ["sync-accounts"],
        queryFn: fetchSyncAccounts,
        refetchInterval: 10000,
    });

    const { data: prefs } = useQuery({
        queryKey: ["preferences"],
        queryFn: fetchPreferences,
    });

    const { data: patterns } = useQuery({
        queryKey: ["learned-patterns"],
        queryFn: fetchLearnedPatterns,
        refetchInterval: 30000,
    });

    const handleSave = async () => {
        if (!configText) return;
        setSaving(true);
        try {
            await saveConfig(configText);
            setSaved(true);
            setTimeout(() => setSaved(false), 2000);
            queryClient.invalidateQueries({ queryKey: ["config"] });
        } finally {
            setSaving(false);
        }
    };

    const handleSavePrefs = async () => {
        if (prefsText === null) return;
        await savePreferences(prefsText);
        setPrefsSaved(true);
        setTimeout(() => setPrefsSaved(false), 2000);
        queryClient.invalidateQueries({ queryKey: ["preferences"] });
    };

    const handleSync = async () => {
        await fetch("/api/sync", { method: "POST" });
    };

    const patternCount = patterns?.length ?? 0;

    return (
        <div className="mx-auto max-w-4xl space-y-6">
            <div>
                <h1 className="text-xl font-bold text-neutral-900 dark:text-neutral-100">
                    Settings
                </h1>
                <p className="mt-1 text-sm text-neutral-500">
                    Configure email accounts, preferences, and AI settings
                </p>
            </div>

            {/* ── Your Preferences ──────────────────────────── */}
            <Section
                title="Your Preferences"
                subtitle="Tell Hermes what you want in plain English. These instructions guide document classification."
                actions={
                    <>
                        {prefsSaved && (
                            <span className="text-xs font-medium text-emerald-600 dark:text-emerald-400">
                                Saved ✓
                            </span>
                        )}
                        <button
                            onClick={handleSavePrefs}
                            disabled={prefsText === null}
                            className="rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-emerald-700 disabled:opacity-40 dark:bg-emerald-700 dark:hover:bg-emerald-600"
                        >
                            Save
                        </button>
                    </>
                }
            >
                <textarea
                    value={prefsText ?? prefs ?? ""}
                    onChange={(e) => setPrefsText(e.target.value)}
                    rows={5}
                    placeholder={
                        'e.g. "Documents from @ato.gov.au are always tax-related. Telstra bills are utility bills."'
                    }
                    className="w-full resize-y rounded-lg border border-neutral-200 bg-neutral-50 px-4 py-3 text-sm text-neutral-900 placeholder-neutral-400 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-200 dark:placeholder-neutral-600"
                />
            </Section>

            {/* ── What Hermes Learned ───────────────────────── */}
            {patternCount > 0 && (
                <Section
                    title="What Hermes Learned"
                    subtitle={`${patternCount} pattern${patternCount !== 1 ? "s" : ""} recognized`}
                    defaultOpen={patternCount <= 10}
                >
                    <div className="overflow-hidden rounded-lg border border-neutral-200 dark:border-neutral-700">
                        <table className="w-full text-left text-xs">
                            <thead>
                                <tr className="border-b border-neutral-200 bg-neutral-50 dark:border-neutral-700 dark:bg-neutral-800">
                                    <th className="px-3 py-2 font-medium text-neutral-600 dark:text-neutral-400">
                                        Sender
                                    </th>
                                    <th className="px-3 py-2 font-medium text-neutral-600 dark:text-neutral-400">
                                        Document Type
                                    </th>
                                    <th className="px-3 py-2 text-right font-medium text-neutral-600 dark:text-neutral-400">
                                        Count
                                    </th>
                                    <th className="px-3 py-2 text-right font-medium text-neutral-600 dark:text-neutral-400">
                                        Confidence
                                    </th>
                                </tr>
                            </thead>
                            <tbody>
                                {patterns!.slice(0, 20).map((p, i) => (
                                    <tr
                                        key={i}
                                        className="border-b border-neutral-100 last:border-0 dark:border-neutral-800"
                                    >
                                        <td className="px-3 py-1.5 text-neutral-500 dark:text-neutral-400">
                                            @{p.senderDomain}
                                        </td>
                                        <td className="px-3 py-1.5 font-medium text-neutral-900 dark:text-neutral-200">
                                            {p.documentType}
                                        </td>
                                        <td className="px-3 py-1.5 text-right tabular-nums text-neutral-500 dark:text-neutral-400">
                                            {p.count}×
                                        </td>
                                        <td className="px-3 py-1.5 text-right tabular-nums text-neutral-500 dark:text-neutral-400">
                                            {(p.avgConfidence * 100).toFixed(0)}%
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                </Section>
            )}

            {/* ── Email Accounts ────────────────────────────── */}
            <Section
                title="Email Accounts"
                actions={
                    <button
                        onClick={handleSync}
                        className="rounded-lg bg-blue-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-blue-700 dark:bg-blue-700 dark:hover:bg-blue-600"
                    >
                        Sync Now
                    </button>
                }
            >
                <div className="space-y-2">
                    {accounts?.map((acc) => (
                        <div
                            key={acc.account}
                            className="flex items-center justify-between rounded-lg bg-neutral-50 px-3 py-2 dark:bg-neutral-800/50"
                        >
                            <div>
                                <div className="text-sm font-medium text-neutral-900 dark:text-neutral-200">
                                    {acc.account}
                                </div>
                                <div className="text-xs text-neutral-500">
                                    {acc.messageCount.toLocaleString()} messages
                                    {acc.lastSyncAt &&
                                        ` · last sync: ${new Date(acc.lastSyncAt).toLocaleString()}`}
                                </div>
                            </div>
                        </div>
                    ))}
                    {(!accounts || accounts.length === 0) && (
                        <p className="text-xs text-neutral-500">
                            No email accounts configured
                        </p>
                    )}
                </div>
            </Section>

            {/* ── Configuration (YAML) ─────────────────────── */}
            <Section
                title="Configuration (YAML)"
                subtitle="Advanced — raw configuration file"
                defaultOpen={false}
                actions={
                    <>
                        {saved && (
                            <span className="text-xs font-medium text-emerald-600 dark:text-emerald-400">
                                Saved ✓
                            </span>
                        )}
                        <button
                            onClick={handleSave}
                            disabled={saving || configText === null}
                            className="rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-emerald-700 disabled:opacity-40 dark:bg-emerald-700 dark:hover:bg-emerald-600"
                        >
                            {saving ? "Saving…" : "Save"}
                        </button>
                    </>
                }
            >
                <textarea
                    value={configText ?? config ?? ""}
                    onChange={(e) => setConfigText(e.target.value)}
                    rows={20}
                    className="w-full resize-y rounded-lg border border-neutral-200 bg-neutral-50 px-4 py-3 font-mono text-xs text-neutral-900 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-neutral-700 dark:bg-neutral-800 dark:text-neutral-200"
                    spellCheck={false}
                />
            </Section>
        </div>
    );
}
