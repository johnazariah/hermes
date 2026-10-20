import { useState } from 'react';
import { access } from '../api/http';
import { useAccess } from '../hooks/useAccess';

export function UnlockReason({ capability = 'mutate' }: { capability?: string }) {
  return <p className="text-sm text-amber-700 dark:text-amber-400">Unlock Hermes with {capability} permission to use these controls.</p>;
}

export function AccessPanel() {
  const state = useAccess();
  const [copied, setCopied] = useState('');
  const [busy, setBusy] = useState(false);
  const copy = async () => {
    try { await navigator.clipboard.writeText(state.pairing?.userCode ?? ''); setCopied('Code copied.'); }
    catch { setCopied('Copy unavailable. Select the displayed code manually.'); }
  };
  const reconnect = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await access.check();
      await access.pair();
    } finally { setBusy(false); }
  };
  return <section aria-label="Hermes access" className="mb-4 rounded-lg border border-neutral-300 dark:border-neutral-700 p-3 text-sm space-y-2">
    <p><strong>Access: {state.phase}</strong>
      {state.phase === 'authenticated' && <> — {state.capabilities.join(', ')}. Expires {new Date(state.expiresAt!).toLocaleTimeString()}.</>}
    </p>
    {state.message && <p>{state.message}</p>}
    {state.phase !== 'authenticated' && <p>Archive browsing and search-only chat work while locked. Configuration, changes and AI calls need owner approval.</p>}
    {state.pairing && <>
      <p>Pairing code: <strong data-testid="pairing-code" className="font-mono select-all">{state.pairing.userCode}</strong>{' '}
        <button onClick={copy}>Copy code</button> {copied}</p>
      <p>In the foreground terminal, as the account running this Service, run:</p>
      <pre className="whitespace-pre-wrap select-all">Hermes.Service pair-ui {state.pairing.userCode} --config-dir &lt;SAME-ABSOLUTE-CONFIG-DIRECTORY&gt; --port {new URL(state.apiOrigin!).port}</pre>
      <p>Use the same configuration directory used to launch this Service (not a different default). No configuration path is exposed here.
        Check the browser origin <code>{window.location.origin}</code>, API audience <code>{state.apiOrigin}</code> and permissions in the terminal, then explicitly confirm.</p>
      <p>Waiting for owner approval. This code expires at {new Date(state.pairing.expiresAt).toLocaleTimeString()}; it is not a password.</p>
    </>}
    {state.phase === 'pairing'
      ? <button onClick={access.cancel}>Cancel pairing</button>
      : state.phase === 'authenticated'
        ? <button onClick={() => { void access.lock(); }}>Lock Hermes</button>
        : <button onClick={() => { void reconnect(); }} disabled={busy || state.phase === 'checking'}>
          {busy || state.phase === 'checking' ? 'Checking access…' : state.phase === 'anonymous' ? 'Unlock Hermes' : 'Reconnect'}
        </button>}
  </section>;
}
