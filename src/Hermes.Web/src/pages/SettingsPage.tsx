import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";

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

interface Suggestion {
    id: number;
    documentId: number;
    proposedCategory: string;
    currentCategory: string;
    confidence: number;
    status: string;
    createdAt: string | null;
    originalName: string | null;
    sender: string | null;
    subject: string | null;
}

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

async function fetchSuggestions(): Promise<Suggestion[]> {
    const res = await fetch("/api/suggestions");
    return res.json();
}

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

    const { data: suggestions } = useQuery({
        queryKey: ["suggestions"],
        queryFn: fetchSuggestions,
        refetchInterval: 10000,
    });

    const approveMutation = useMutation({
        mutationFn: (id: number) =>
            fetch(`/api/suggestions/${id}/approve`, { method: "POST" }).then((r) => r.json()),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["suggestions"] }),
    });

    const rejectMutation = useMutation({
        mutationFn: (id: number) =>
            fetch(`/api/suggestions/${id}/reject`, { method: "POST" }).then((r) => r.json()),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ["suggestions"] }),
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

    const pendingSuggestions = suggestions?.filter((s) => s.status === "pending") ?? [];

    return (
        <div className="max-w-4xl mx-auto space-y-8">
            <div>
                <h1 className="text-xl font-bold mb-1">Settings</h1>
                <p className="text-sm text-neutral-500">
                    Configure email accounts, preferences, and AI settings
                </p>
            </div>

            {/* Suggestions review */}
            {pendingSuggestions.length > 0 && (
                <div className="rounded-xl border border-amber-700/50 bg-amber-900/20 p-5">
                    <div className="text-sm font-semibold text-amber-300 mb-3">
                        Review Suggestions ({pendingSuggestions.length})
                    </div>
                    <p className="text-xs text-neutral-400 mb-3">
                        Hermes is unsure about these classifications. Approve to confirm or reject to dismiss.
                    </p>
                    <div className="space-y-2">
                        {pendingSuggestions.map((s) => (
                            <div
                                key={s.id}
                                className="flex items-center justify-between px-3 py-2 bg-neutral-800/50 rounded-lg"
                            >
                                <div className="flex-1 min-w-0">
                                    <div className="text-sm text-neutral-200 truncate">
                                        {s.originalName ?? `Document #${s.documentId}`}
                                    </div>
                                    <div className="text-xs text-neutral-500">
                                        Suggested: <span className="text-amber-400">{s.proposedCategory}</span>
                                        {" · "}confidence: {(s.confidence * 100).toFixed(0)}%
                                        {s.sender && ` · from: ${s.sender}`}
                                    </div>
                                </div>
                                <div className="flex gap-2 ml-3">
                                    <button
                                        onClick={() => approveMutation.mutate(s.id)}
                                        className="px-2 py-1 rounded bg-green-700 text-white text-xs hover:bg-green-600"
                                    >
                                        ✓
                                    </button>
                                    <button
                                        onClick={() => rejectMutation.mutate(s.id)}
                                        className="px-2 py-1 rounded bg-red-700 text-white text-xs hover:bg-red-600"
                                    >
                                        ✗
                                    </button>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* Your preferences */}
            <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-5">
                <div className="flex items-center justify-between mb-3">
                    <div>
                        <div className="text-sm font-semibold text-neutral-300">
                            Your Preferences
                        </div>
                        <div className="text-xs text-neutral-500 mt-0.5">
                            Tell Hermes what you want in plain English. These instructions guide document classification.
                        </div>
                    </div>
                    <div className="flex gap-2">
                        {prefsSaved && (
                            <span className="text-xs text-green-400">Saved ✓</span>
                        )}
                        <button
                            onClick={handleSavePrefs}
                            disabled={prefsText === null}
                            className="px-3 py-1.5 rounded-lg bg-green-600 text-white text-xs hover:bg-green-500 disabled:opacity-40 transition-colors"
                        >
                            Save
                        </button>
                    </div>
                </div>
                <textarea
                    value={prefsText ?? prefs ?? ""}
                    onChange={(e) => setPrefsText(e.target.value)}
                    rows={5}
                    placeholder='e.g. "Documents from @ato.gov.au are always tax-related. Telstra bills are utility bills."'
                    className="w-full px-4 py-3 rounded-lg bg-neutral-900 border border-neutral-700 text-neutral-300 text-sm resize-y focus:outline-none focus:ring-1 focus:ring-blue-500"
                />
            </div>

            {/* What Hermes learned */}
            {patterns && patterns.length > 0 && (
                <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-5">
                    <div className="text-sm font-semibold text-neutral-300 mb-3">
                        What Hermes Learned
                    </div>
                    <div className="space-y-1">
                        {patterns.slice(0, 20).map((p, i) => (
                            <div
                                key={i}
                                className="flex items-center justify-between px-3 py-1.5 text-xs"
                            >
                                <span className="text-neutral-400">
                                    @{p.senderDomain} → <span className="text-neutral-200">{p.documentType}</span>
                                </span>
                                <span className="text-neutral-600">
                                    {p.count}× · {(p.avgConfidence * 100).toFixed(0)}% avg
                                </span>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* Sync accounts */}
            <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-5">
                <div className="flex items-center justify-between mb-4">
                    <div className="text-sm font-semibold text-neutral-300">
                        Email Accounts
                    </div>
                    <button
                        onClick={handleSync}
                        className="px-3 py-1.5 rounded-lg bg-blue-600 text-white text-xs hover:bg-blue-500 transition-colors"
                    >
                        Sync Now
                    </button>
                </div>
                <div className="space-y-2">
                    {accounts?.map((acc) => (
                        <div
                            key={acc.account}
                            className="flex items-center justify-between px-3 py-2 bg-neutral-800/50 rounded-lg"
                        >
                            <div>
                                <div className="text-sm text-neutral-200">
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
                        <div className="text-xs text-neutral-600">
                            No email accounts configured
                        </div>
                    )}
                </div>
            </div>

            {/* Config editor */}
            <div className="rounded-xl border border-neutral-800 bg-neutral-900/50 p-5">
                <div className="flex items-center justify-between mb-4">
                    <div className="text-sm font-semibold text-neutral-300">
                        Configuration (YAML)
                    </div>
                    <div className="flex gap-2">
                        {saved && (
                            <span className="text-xs text-green-400">
                                Saved ✓
                            </span>
                        )}
                        <button
                            onClick={handleSave}
                            disabled={saving || configText === null}
                            className="px-3 py-1.5 rounded-lg bg-green-600 text-white text-xs hover:bg-green-500 disabled:opacity-40 transition-colors"
                        >
                            {saving ? "Saving..." : "Save"}
                        </button>
                    </div>
                </div>
                <textarea
                    value={configText ?? config ?? ""}
                    onChange={(e) => setConfigText(e.target.value)}
                    rows={20}
                    className="w-full px-4 py-3 rounded-lg bg-neutral-900 border border-neutral-700 text-neutral-300 font-mono text-xs resize-y focus:outline-none focus:ring-1 focus:ring-blue-500"
                    spellCheck={false}
                />
            </div>
        </div>
    );
}
