import { useState } from 'react';
import { Shell } from './components/layout/Shell';
import { useQuery } from '@tanstack/react-query';
import { fetchStats } from './api/hermes';
import { DocumentList } from './components/documents/DocumentList';
import { DocumentDetail } from './components/documents/DocumentDetail';
import { SettingsDialog } from './components/settings/SettingsDialog';
import { DeadLetterPanel } from './components/pipeline/DeadLetterPanel';

function WelcomePage() {
  const { data: stats } = useQuery({ queryKey: ['stats'], queryFn: fetchStats, refetchInterval: 10000 });

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h1 className="text-2xl font-bold mb-2">Hermes</h1>
        <p className="text-neutral-400 mb-6">Document Intelligence</p>
      </div>

      {stats && (
        <div className="grid grid-cols-2 gap-4">
          <Stat label="Documents" value={stats.documentCount.toLocaleString()} />
          <Stat label="Extracted" value={stats.extractedCount.toLocaleString()} />
          <Stat label="Embedded" value={stats.embeddedCount.toLocaleString()} />
          <Stat label="Database" value={`${stats.databaseSizeMb.toFixed(1)} MB`} />
        </div>
      )}

      <DeadLetterPanel />
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-neutral-900 rounded-lg p-4 border border-neutral-800">
      <div className="text-xs text-neutral-500 uppercase tracking-wider">{label}</div>
      <div className="text-xl font-semibold mt-1">{value}</div>
    </div>
  );
}

export default function App() {
  const [selectedCategory, setSelectedCategory] = useState<string | null>(null);
  const [selectedDocId, setSelectedDocId] = useState<number | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);

  let content;
  if (selectedDocId != null) {
    content = <DocumentDetail documentId={selectedDocId} onBack={() => setSelectedDocId(null)} />;
  } else if (selectedCategory) {
    content = <DocumentList category={selectedCategory} onSelectDocument={setSelectedDocId} />;
  } else {
    content = <WelcomePage />;
  }

  return (
    <>
      <Shell
        selectedCategory={selectedCategory}
        onSelectCategory={(cat) => { setSelectedCategory(cat); setSelectedDocId(null); }}
        onSelectDocument={setSelectedDocId}
        onOpenSettings={() => setSettingsOpen(true)}
      >
        {content}
      </Shell>
      <SettingsDialog open={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </>
  );
}
