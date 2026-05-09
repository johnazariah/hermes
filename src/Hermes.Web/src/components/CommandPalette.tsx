import { useEffect, useState } from "react";
import { Command } from "cmdk";
import { useNavigate } from "react-router-dom";
import { triggerSync } from "../api/hermes";

export function CommandPalette() {
    const [open, setOpen] = useState(false);
    const navigate = useNavigate();

    useEffect(() => {
        const onKeyDown = (e: KeyboardEvent) => {
            if (e.key === "k" && (e.metaKey || e.ctrlKey)) {
                e.preventDefault();
                setOpen((prev) => !prev);
            }
        };
        document.addEventListener("keydown", onKeyDown);
        return () => document.removeEventListener("keydown", onKeyDown);
    }, []);

    const go = (path: string) => {
        navigate(path);
        setOpen(false);
    };

    const handleSync = () => {
        void triggerSync();
        setOpen(false);
    };

    if (!open) return null;

    return (
        <div className="fixed inset-0 z-[100]">
            {/* Backdrop */}
            <div
                className="absolute inset-0 bg-black/50 backdrop-blur-sm"
                onClick={() => setOpen(false)}
            />

            {/* Palette */}
            <div className="relative flex items-start justify-center pt-[20vh]">
                <Command
                    className="w-full max-w-lg rounded-xl border border-neutral-700 dark:border-neutral-700 border-neutral-300 bg-neutral-900 dark:bg-neutral-900 bg-white shadow-2xl overflow-hidden"
                    onKeyDown={(e: React.KeyboardEvent) => {
                        if (e.key === "Escape") setOpen(false);
                    }}
                >
                    <Command.Input
                        placeholder="Type a command or search…"
                        className="w-full px-4 py-3 text-sm bg-transparent border-b border-neutral-700 dark:border-neutral-700 border-neutral-200 text-neutral-100 dark:text-neutral-100 text-neutral-900 placeholder:text-neutral-500 outline-none"
                        autoFocus
                    />
                    <Command.List className="max-h-72 overflow-y-auto p-2">
                        <Command.Empty className="px-4 py-6 text-sm text-neutral-500 text-center">
                            No results found.
                        </Command.Empty>

                        <Command.Group
                            heading="Navigation"
                            className="text-xs text-neutral-500 px-2 py-1.5 font-medium"
                        >
                            <Command.Item
                                onSelect={() => go("/")}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                🏠 Home
                            </Command.Item>
                            <Command.Item
                                onSelect={() => go("/documents")}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                📄 Documents
                            </Command.Item>
                            <Command.Item
                                onSelect={() => go("/search")}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                🔍 Search
                            </Command.Item>
                            <Command.Item
                                onSelect={() => go("/settings")}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                ⚙️ Settings
                            </Command.Item>
                        </Command.Group>

                        <Command.Separator className="h-px bg-neutral-800 dark:bg-neutral-800 bg-neutral-200 my-1" />

                        <Command.Group
                            heading="Actions"
                            className="text-xs text-neutral-500 px-2 py-1.5 font-medium"
                        >
                            <Command.Item
                                onSelect={handleSync}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                📨 Sync email now
                            </Command.Item>
                            <Command.Item
                                onSelect={() => go("/")}
                                className="px-3 py-2 text-sm rounded-lg cursor-pointer text-neutral-200 dark:text-neutral-200 text-neutral-800 data-[selected=true]:bg-neutral-800 dark:data-[selected=true]:bg-neutral-800 data-[selected=true]:bg-neutral-100"
                            >
                                📊 Show pipeline status
                            </Command.Item>
                        </Command.Group>
                    </Command.List>

                    <div className="border-t border-neutral-700 dark:border-neutral-700 border-neutral-200 px-4 py-2 text-[11px] text-neutral-500 flex gap-4">
                        <span>↑↓ Navigate</span>
                        <span>↵ Select</span>
                        <span>esc Close</span>
                    </div>
                </Command>
            </div>
        </div>
    );
}
