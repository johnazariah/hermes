import { AccessClient } from './access.ts';

const selectedApiOrigin = import.meta.env?.VITE_HERMES_API_ORIGIN as string | undefined;
const browserOrigin = typeof window === 'undefined' ? 'http://127.0.0.1:21742' : window.location.origin;
const apiAudience = import.meta.env?.VITE_HERMES_API_AUDIENCE as string | undefined;
const directAudience = `http://127.0.0.1:${new URL(browserOrigin).port || '80'}`;
export const access = new AccessClient({
  browserOrigin,
  apiOrigin: selectedApiOrigin || undefined,
  apiAudience: apiAudience || selectedApiOrigin || (import.meta.env?.DEV ? 'http://127.0.0.1:21742' : directAudience),
  postNative: message => {
    const host = (window as Window & { chrome?: { webview?: { postMessage: (message: object) => void } } }).chrome?.webview;
    host?.postMessage(message);
  },
});

export const request = access.request;
