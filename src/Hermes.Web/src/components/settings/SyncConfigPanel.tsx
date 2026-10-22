import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { access as authority, request } from "../../api/http";
import { triggerSync } from "../../api/hermes";
import { useAccess } from "../../hooks/useAccess";
import { UnlockReason } from "../AccessPanel";

interface SyncAccount {
    account: string;
    lastSyncAt: string | null;
    messageCount: number;
}

export function SyncConfigPanel() {
    const access = useAccess();
    const [feedback, setFeedback] = useState<{ error: boolean; message: string } | null>(null);
    const queryClient = useQueryClient();
    const { data: accounts, error: accountsError, isPending } = useQuery<SyncAccount[]>({
        queryKey: ["sync-accounts"],
        queryFn: () => request("/api/sync/accounts").then((r) => r.json()),
        enabled: access.can('config.read'),
        refetchInterval: 10000,
    });

    const [resetting, setResetting] = useState<string | null>(null);

    const resetAccount = async (account: string, fromDate: string) => {
        if (resetting !== null) return;
        const generation = access.generation;
        setResetting(account);
        setFeedback(null);
        try {
            await request("/api/sync/reset", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ account, from: fromDate }),
            });
            if (!authority.isCurrent(generation)) return;
            queryClient.invalidateQueries({ queryKey: ["sync-accounts"] });
            await triggerSync();
            if (authority.isCurrent(generation)) setFeedback({ error: false, message: 'Reset saved; sync request accepted, not completed.' });
        } catch (error) {
            if (authority.isCurrent(generation)) setFeedback({ error: true, message: error instanceof Error ? error.message : 'Reset failed.' });
        } finally { if (authority.isCurrent(generation)) setResetting(null); }
    };

    if (!access.can('config.read')) return <UnlockReason capability="config.read" />;
    if (accountsError) return <p role="alert">Unable to load sync accounts: {accountsError.message}</p>;
    if (isPending) return <p role="status">Loading sync accounts…</p>;
    if (accounts.length === 0) {
        return (
            <div className="bg-neutral-900 border border-neutral-800 rounded-lg p-4">
                {!access.can('mutate') && <UnlockReason />}
                {feedback && <p role={feedback.error ? "alert" : "status"}>{feedback.message}</p>}
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
            {!access.can('mutate') && <UnlockReason />}
            {feedback && <p role={feedback.error ? "alert" : "status"}>{feedback.message}</p>}
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
                                disabled={!access.can('mutate') || resetting !== null}
                                className="text-xs px-2 py-1 bg-neutral-800 rounded hover:bg-neutral-700 disabled:opacity-50"
                            >
                                {resetting === a.account
                                    ? "..."
                                    : "📅 Set from"}
                            </button>
                            <button
                                onClick={() => resetAccount(a.account, "")}
                                disabled={!access.can('mutate') || resetting !== null}
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
