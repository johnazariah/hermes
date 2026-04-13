import { useState, useEffect } from 'react';

export function SettingsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [yaml, setYaml] = useState('');
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (open) {
      fetch('/api/settings')
        .then(r => r.text())
        .then(setYaml)
        .catch(() => setYaml('# Failed to load config'));
    }
  }, [open]);

  if (!open) return null;

  const handleSave = async () => {
    setLoading(true);
    try {
      await fetch('/api/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'text/yaml' },
        body: yaml,
      });
      onClose();
    } catch { /* show error */ }
    finally { setLoading(false); }
  };

  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50" onClick={onClose}>
      <div className="bg-neutral-900 border border-neutral-700 rounded-xl w-[600px] max-h-[80vh] flex flex-col" onClick={e => e.stopPropagation()}>
        <div className="px-6 py-4 border-b border-neutral-800 flex items-center justify-between">
          <h2 className="text-lg font-semibold">Settings</h2>
          <button onClick={onClose} className="text-neutral-500 hover:text-neutral-300">✕</button>
        </div>
        <div className="flex-1 overflow-y-auto p-6">
          <label className="text-xs text-neutral-500 uppercase tracking-wider mb-2 block">config.yaml</label>
          <textarea
            value={yaml}
            onChange={e => setYaml(e.target.value)}
            className="w-full h-80 bg-neutral-950 border border-neutral-700 rounded-lg p-4 text-sm font-mono leading-relaxed focus:outline-none focus:border-blue-500 resize-none"
            spellCheck={false}
          />
        </div>
        <div className="px-6 py-4 border-t border-neutral-800 flex justify-end gap-3">
          <button onClick={onClose} className="px-4 py-2 text-sm text-neutral-400 hover:text-neutral-200">Cancel</button>
          <button
            onClick={handleSave}
            disabled={loading}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-500 disabled:opacity-50"
          >
            {loading ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
