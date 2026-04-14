import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

interface SyncAccount {
    account: string;
    lastSyncAt: string | null;
    messageCount: number;
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

export function SettingsPage() {
    const [configText, setConfigText] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [saved, setSaved] = useState(false);
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

    const handleSync = async () => {
        await fetch("/api/sync", { method: "POST" });
    };

    return (
        <div className="max-w-4xl mx-auto space-y-8">
            <div>
                <h1 className="text-xl font-bold mb-1">Settings</h1>
                <p className="text-sm text-neutral-500">
                    Configure email accounts, watch folders, and AI settings
                </p>
            </div>

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
