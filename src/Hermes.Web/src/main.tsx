import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ThemeProvider } from './hooks/useTheme'
import './index.css'
import { AccessApp } from './components/AccessApp'
import { access } from './api/http'

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
let generation = access.getSnapshot().generation
access.subscribe(() => {
  const next = access.getSnapshot().generation
  if (generation === next) return
  generation = next
  void queryClient.cancelQueries()
  queryClient.clear()
})

type NativeHost = { addEventListener: (type: 'message', handler: (event: MessageEvent) => void) => void }
const host = (window as Window & { chrome?: { webview?: NativeHost } }).chrome?.webview
host?.addEventListener('message', event => { access.acceptNative(event.data) })
window.addEventListener('focus', () => { void access.check() })
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') void access.check() })
window.addEventListener('pagehide', () => { access.dispose() })
window.addEventListener('pageshow', event => { if (event.persisted) window.location.reload() })
void access.check()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ThemeProvider>
        <AccessApp />
      </ThemeProvider>
    </QueryClientProvider>
  </StrictMode>,
)
