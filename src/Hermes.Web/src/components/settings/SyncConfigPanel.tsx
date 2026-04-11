import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";

interface SyncAccount {
    account: string;
    lastSyncAt: string | null;
    messageCount: number;
}

export function SyncConfigPanel() {
    const queryClient = useQueryClient();
    const { data: accounts } = useQuery<SyncAccount[]>({
        queryKey: ["sync-accounts"],
        queryFn: () => fetch("/api/sync/accounts").then((r) => r.json()),
        refetchInterval: 10000,
    });

    const [resetting, setResetting] = useState<string | null>(null);

    const resetAccount = async (account: string, fromDate: string) => {
        setResetting(account);
        await fetch("/api/sync/reset", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ account, from: fromDate }),
        });
        queryClient.invalidateQueries({ queryKey: ["sync-accounts"] });
        // Trigger a sync
        await fetch("/api/sync", { method: "POST" });
        setResetting(null);
    };

    if (!accounts || accounts.length === 0) {
        return (
            <div className="bg-neutral-900 border border-neutral-800 rounded-lg p-4">
                <div className="text-xs text-neutral-500 uppercase tracking-wider mb-2">
                    Email Sync
                </div>
                <div className="text-sm text-neutral-500">
                    No accounts configured.
                </div>
            </div>
        );
    }

    return (
        <div className="bg-neutral-900 border border-neutral-800 rounded-lg p-4">
            <div className="text-xs text-neutral-500 uppercase tracking-wider mb-3">
                Email Sync
            </div>
            <div className="space-y-3">
                {accounts.map((a) => (
                    <div key={a.account} className="flex items-center gap-3">
                        <div className="flex-1 min-w-0">
                            <div className="text-sm truncate">
                                📧 {a.account}
                            </div>
                            <div className="text-xs text-neutral-500">
                                {a.lastSyncAt
                                    ? `Last sync: ${new Date(a.lastSyncAt).toLocaleDateString()}`
                                    : "Not synced yet"}
                                {a.messageCount > 0 &&
                                    ` · ${a.messageCount} messages`}
                            </div>
                        </div>
                        <div className="flex gap-1">
                            <button
                                onClick={() => {
                                    const from = prompt(
                                        `Pull emails from date (YYYY-MM-DD):\nDefault: 2 fiscal years back`,
                                        "2024-06-01",
                                    );
                                    if (from !== null)
                                        resetAccount(a.account, from);
                                }}
                                disabled={resetting === a.account}
                                className="text-xs px-2 py-1 bg-neutral-800 rounded hover:bg-neutral-700 disabled:opacity-50"
                            >
                                {resetting === a.account
                                    ? "..."
                                    : "📅 Set from"}
                            </button>
                            <button
                                onClick={() => resetAccount(a.account, "")}
                                disabled={resetting === a.account}
                                className="text-xs px-2 py-1 bg-neutral-800 rounded hover:bg-neutral-700 disabled:opacity-50"
                            >
                                🔄 Reset
                            </button>
                        </div>
                    </div>
                ))}
            </div>
        </div>
    );
}
