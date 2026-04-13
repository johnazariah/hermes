import { useState } from "react";
import { Shell } from "./components/layout/Shell";
import type { ViewType } from "./components/layout/Shell";
import { DocumentList } from "./components/documents/DocumentList";
import { DocumentDetail } from "./components/documents/DocumentDetail";
import { SettingsDialog } from "./components/settings/SettingsDialog";
import { DeadLetterPanel } from "./components/pipeline/DeadLetterPanel";
import { ActivityPanel } from "./components/pipeline/ActivityPanel";
import { SyncConfigPanel } from "./components/settings/SyncConfigPanel";
import { PipelineDashboard } from "./components/pipeline/PipelineDashboard";

function WelcomePage() {
    return (
        <div className="max-w-3xl space-y-6">
            <div>
                <h1 className="text-2xl font-bold mb-2">Hermes</h1>
                <p className="text-neutral-400 mb-4">Document Intelligence</p>
            </div>
            <PipelineDashboard />
            <DeadLetterPanel />
            <SyncConfigPanel />
        </div>
    );
}

export default function App() {
    const [selectedView, setSelectedView] = useState<ViewType>(null);
    const [selectedDocId, setSelectedDocId] = useState<number | null>(null);
    const [settingsOpen, setSettingsOpen] = useState(false);

    let content;
    if (selectedDocId != null) {
        content = (
            <DocumentDetail
                documentId={selectedDocId}
                onBack={() => setSelectedDocId(null)}
            />
        );
    } else if (selectedView?.kind === "category") {
        content = (
            <DocumentList
                category={selectedView.value}
                onSelectDocument={setSelectedDocId}
            />
        );
    } else if (
        selectedView?.kind === "smart" &&
        selectedView.value === "review"
    ) {
        content = (
            <DocumentList
                category="unsorted"
                onSelectDocument={setSelectedDocId}
            />
        );
    } else if (
        selectedView?.kind === "smart" &&
        selectedView.value === "starred"
    ) {
        content = (
            <div className="text-neutral-500 text-sm">
                Starred documents — coming soon
            </div>
        );
    } else if (
        selectedView?.kind === "smart" &&
        selectedView.value === "recent"
    ) {
        content = (
            <div className="text-neutral-500 text-sm">
                Recent documents — coming soon
            </div>
        );
    } else if (
        selectedView?.kind === "smart" &&
        selectedView.value === "activity"
    ) {
        content = <ActivityPanel />;
    } else {
        content = <WelcomePage />;
    }

    return (
        <>
            <Shell
                selectedView={selectedView}
                onSelectView={(view) => {
                    setSelectedView(view);
                    setSelectedDocId(null);
                }}
                onSelectDocument={setSelectedDocId}
                onOpenSettings={() => setSettingsOpen(true)}
            >
                {content}
            </Shell>
            <SettingsDialog
                open={settingsOpen}
                onClose={() => setSettingsOpen(false)}
            />
        </>
    );
}
