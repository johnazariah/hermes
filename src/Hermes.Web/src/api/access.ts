export type Capability = 'archive.read' | 'config.read' | 'model.invoke' | 'mutate';
export const requestedCapabilities = ['config.read', 'model.invoke', 'mutate'] as const;
export type AccessPhase = 'checking' | 'anonymous' | 'pairing' | 'authenticated' | 'expired' | 'unavailable';
export interface AccessState {
  phase: AccessPhase;
  generation: number;
  instanceId?: string;
  apiOrigin?: string;
  capabilities: readonly Capability[];
  expiresAt?: string;
  pairing?: { pairingId: string; userCode: string; expiresAt: string };
  message?: string;
}
type Json = Record<string, unknown>;
type NativeReady = { version: 1; type: 'hermes.access.ready'; navigationId: string; instanceId: string };
interface Options {
  browserOrigin: string;
  apiOrigin?: string;
  apiAudience?: string;
  fetch?: typeof fetch;
  crypto?: Crypto;
  now?: () => number;
  utcNow?: () => number;
  postNative?: (message: object) => void;
}
export interface RequestPolicy {
  capabilities?: readonly Capability[];
  // Only explicit search-only chat is an anonymous POST.
  searchOnly?: boolean;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly requiredCapabilities: readonly Capability[];
  readonly retryAfter: string | null;
  constructor(status: number, code: string, message: string, required: readonly Capability[] = [], retryAfter: string | null = null) {
    super(`${message} (HTTP ${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.requiredCapabilities = required;
    this.retryAfter = retryAfter;
  }
}

function object(value: unknown): value is Json {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function exact(value: unknown, fields: string[]): asserts value is Json {
  if (!object(value) || Object.keys(value).sort().join(',') !== fields.sort().join(',')) {
    throw new Error('The Service returned an invalid access response.');
  }
}
function base64url(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}
function canonical(value: unknown, bytes: number): value is string {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)) return false;
  try {
    const decoded = Uint8Array.from(atob(value.replaceAll('-', '+').replaceAll('_', '/')), c => c.charCodeAt(0));
    return decoded.length === bytes && base64url(decoded) === value;
  } catch { return false; }
}
function capabilities(value: unknown, effective: boolean): value is Capability[] {
  const allowed: readonly string[] = effective ? ['archive.read', ...requestedCapabilities] : requestedCapabilities;
  return Array.isArray(value) && value.every(v => typeof v === 'string' && allowed.includes(v))
    && new Set(value).size === value.length && [...value].sort().join(',') === value.join(',')
    && (!effective || value.includes('archive.read'));
}
function timestamp(value: unknown): value is string {
  return typeof value === 'string' && /^\d{4}-\d\d-\d\dT.*(?:Z|[+-]\d\d:\d\d)$/.test(value)
    && Number.isFinite(Date.parse(value));
}
function audience(value: unknown): value is string {
  if (typeof value !== 'string') return false;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' && url.hostname === '127.0.0.1' && url.port !== ''
      && url.origin === value && !url.username && !url.password;
  } catch { return false; }
}

// JSON.parse alone silently accepts duplicate properties. Validate structure first.
export function strictJson(text: string): unknown {
  let offset = 0;
  const space = () => { while (/\s/.test(text[offset] ?? '') && offset < text.length) offset++; };
  const string = (): string => {
    const start = offset++;
    while (offset < text.length) {
      if (text[offset++] === '"') return JSON.parse(text.slice(start, offset)) as string;
      if (text[offset - 1] === '\\') offset++;
    }
    throw new Error('Invalid JSON string');
  };
  const value = (depth: number): void => {
    space();
    if (depth > 8) throw new Error('Access JSON exceeds the depth limit');
    if (text[offset] === '"') { string(); return; }
    const opener = text[offset];
    if (opener === '{' || opener === '[') {
      offset++; space();
      const end = opener === '{' ? '}' : ']';
      const keys = new Set<string>();
      if (text[offset] === end) { offset++; return; }
      while (offset < text.length) {
        if (opener === '{') {
          space();
          if (text[offset] !== '"') throw new Error('Invalid JSON property');
          const key = string();
          if (keys.has(key)) throw new Error('Duplicate JSON property');
          keys.add(key); space();
          if (text[offset++] !== ':') throw new Error('Invalid JSON property');
        }
        value(depth + 1); space();
        const separator = text[offset++];
        if (separator === end) return;
        if (separator !== ',') throw new Error('Invalid JSON separator');
      }
      throw new Error('Incomplete JSON');
    }
    const literal = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(offset));
    if (!literal) throw new Error('Invalid JSON value');
    offset += literal[0].length;
  };
  if (text.charCodeAt(0) === 0xfeff) throw new Error('JSON BOM is not allowed');
  value(1); space();
  if (offset !== text.length) throw new Error('Trailing JSON');
  return JSON.parse(text) as unknown;
}

async function protocolJson(response: Response): Promise<unknown> {
  if (!response.headers.get('content-type')?.startsWith('application/json')) throw new Error('Expected access JSON.');
  if (!response.body) throw new Error('Missing access response.');
  const reader = response.body.getReader();
  // Preserve the BOM so strictJson can reject it, including split byte sequences.
  const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  let text = '';
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.length;
      if (size > 4096) throw new Error('Access response exceeds the size limit.');
      text += decoder.decode(value, { stream: true });
    }
    return strictJson(text + decoder.decode());
  } catch {
    throw new Error('The Service returned invalid access JSON.');
  } finally { await reader.cancel(); reader.releaseLock(); }
}

function stale(): DOMException { return new DOMException('Obsolete access operation', 'AbortError'); }

export class AccessClient {
  private state: AccessState = { phase: 'checking', generation: 0, capabilities: [] };
  private listeners = new Set<() => void>();
  private token?: string;
  private verifier?: string;
  private native?: NativeReady;
  private waitingNative?: NativeReady;
  private rejectedNavigations = new Set<string>();
  private controllers = new Set<AbortController>();
  private pollTimer?: ReturnType<typeof setTimeout>;
  private expiryTimer?: ReturnType<typeof setTimeout>;
  private deadline = 0;
  private pairDeadline = 0;
  private checking?: Promise<void>;
  private disposed = false;
  private readonly options: Options;
  private readonly fetcher: typeof fetch;
  private readonly clock: () => number;
  private readonly utc: () => number;

  constructor(options: Options) {
    if (new URL(options.browserOrigin).origin !== options.browserOrigin
      || (options.apiOrigin !== undefined && !audience(options.apiOrigin))
      || (options.apiAudience !== undefined && !audience(options.apiAudience))
      || (options.apiOrigin && options.apiAudience && options.apiOrigin !== options.apiAudience)) throw new Error('Invalid Hermes target.');
    this.options = options;
    this.fetcher = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.clock = options.now ?? (() => performance.now());
    this.utc = options.utcNow ?? Date.now;
  }
  getSnapshot = (): AccessState => this.state;
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  private publish(update: Partial<AccessState>): void {
    if (this.disposed) return;
    this.state = { ...this.state, ...update };
    this.listeners.forEach(listener => listener());
  }
  private invalidate(): void {
    this.controllers.forEach(controller => controller.abort());
    this.controllers.clear();
    clearTimeout(this.pollTimer); clearTimeout(this.expiryTimer);
    this.checking = undefined;
  }
  private clear(phase: AccessPhase, message?: string): void {
    this.token = undefined; this.verifier = undefined;
    this.deadline = 0; this.pairDeadline = 0;
    this.invalidate();
    this.publish({ generation: this.state.generation + 1, phase, capabilities: [], expiresAt: undefined, pairing: undefined, message });
  }
  private current(generation: number): void {
    if (this.disposed || generation !== this.state.generation) throw stale();
  }
  isCurrent = (generation: number): boolean => !this.disposed && generation === this.state.generation;
  can = (capability: Capability): boolean => {
    if (this.token && this.clock() >= this.deadline) this.clear('expired', 'Access expired. Reconnect to continue.');
    return this.state.phase === 'authenticated' && this.state.capabilities.includes(capability);
  };
  private path(path: string): string {
    if (!/^\/api\/[A-Za-z0-9]/.test(path) || /[\\#]/.test(path) || [...path].some(c => c.charCodeAt(0) <= 32)) throw new Error('Only relative Hermes /api/ requests are allowed.');
    const origin = this.options.apiOrigin ?? this.options.browserOrigin;
    const url = new URL(path, origin);
    if (url.origin !== origin || !url.pathname.startsWith('/api/') || url.pathname !== path.split('?')[0]
      || /^\/api\/mcp(?:\/|$)/i.test(url.pathname) || /%2f|%5c|%2e/i.test(url.pathname)) {
      throw new Error('Invalid Hermes API transport.');
    }
    return this.options.apiOrigin ? url.href : path;
  }
  private async send(path: string, init: RequestInit, authorised: boolean, generation: number,
    credential = this.token, clearOn401 = true): Promise<Response> {
    const headers = new Headers(init.headers);
    if (headers.has('Authorization')) throw new Error('Caller Authorization overrides are not allowed.');
    const target = this.path(path);
    if (authorised && credential) headers.set('Authorization', `Bearer ${credential}`);
    const controller = new AbortController();
    const signals = [controller.signal, ...(init.signal ? [init.signal] : [])];
    if (path.startsWith('/api/access')) signals.push(AbortSignal.timeout(10_000));
    const signal = AbortSignal.any(signals);
    this.controllers.add(controller);
    try {
      const response = await this.fetcher(target, {
        ...init, headers, signal, credentials: 'omit', redirect: 'error', cache: 'no-store',
      });
      this.current(generation);
      signal.throwIfAborted();
      if (response.redirected || (response.status >= 300 && response.status < 400)) throw new Error('Hermes redirects are not allowed.');
      if (!response.ok) {
        const error = await this.error(response);
        this.current(generation);
        if (response.status === 401 && clearOn401) this.clear(error.code === 'credential_expired' ? 'expired' : 'anonymous', error.message);
        throw error;
      }
      if (response.status === 204 || response.status === 205 || init.method?.toUpperCase() === 'HEAD') {
        await response.body?.cancel();
        this.current(generation);
        signal.throwIfAborted();
        this.controllers.delete(controller);
        return new Response(null, { status: response.status, statusText: response.statusText, headers: response.headers });
      }
      if (!response.body) { this.controllers.delete(controller); return response; }
      const reader = response.body.getReader();
      const abortBody = () => { void reader.cancel().catch(() => {}); };
      signal.addEventListener('abort', abortBody, { once: true });
      const release = () => {
        this.controllers.delete(controller);
        signal.removeEventListener('abort', abortBody);
      };
      const body = new ReadableStream<Uint8Array>({
        pull: async sink => {
          try {
            this.current(generation);
            signal.throwIfAborted();
            const next = await reader.read();
            this.current(generation);
            signal.throwIfAborted();
            if (next.done) { release(); sink.close(); reader.releaseLock(); }
            else sink.enqueue(next.value);
          } catch (error) {
            release();
            try { await reader.cancel(); } catch { /* The upstream fetch may already be aborted. */ }
            finally { reader.releaseLock(); }
            sink.error(error);
          }
        },
        cancel: async () => { controller.abort(); release(); await reader.cancel(); reader.releaseLock(); },
      });
      return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
    } catch (error) {
      this.controllers.delete(controller);
      throw error;
    }
  }
  private async error(response: Response): Promise<ApiError> {
    let code = 'service_error';
    let message = 'Service request failed';
    let required: Capability[] = [];
    try {
      const value: unknown = await response.json();
      if (object(value)) {
        code = typeof value.error === 'string' ? value.error : code;
        message = typeof value.message === 'string' ? value.message
          : typeof value.detail === 'string' ? value.detail : message;
        if (capabilities(value.requiredCapabilities, false)) required = value.requiredCapabilities;
      }
    } catch { /* Non-JSON errors still retain their HTTP status. */ }
    return new ApiError(response.status, code, message, required, response.headers.get('Retry-After'));
  }
  request = async (path: string, init: RequestInit = {}, policy: RequestPolicy = {}): Promise<Response> => {
    this.path(path);
    if (/^\/api\/access(?:\/|$|\?)/.test(path)) throw new Error('Access protocol is managed by the access owner.');
    const method = (init.method ?? 'GET').toUpperCase();
    const isConfig = /^\/api\/(?:settings|preferences|sync\/accounts)(?:\/|$|\?)/.test(path);
    const chat = /^\/api\/chat(?:\?|$)/.test(path);
    const search = /^\/api\/search(?:\?|$)/.test(path);
    let required: readonly Capability[] = policy.capabilities ?? [];
    if (isConfig && ['GET', 'HEAD'].includes(method)) required = [...required, 'config.read'];
    if (!['GET', 'HEAD'].includes(method) && !chat) required = [...required, 'mutate'];
    if (chat) {
      const value: unknown = typeof init.body === 'string' ? strictJson(init.body) : null;
      if (!object(value) || typeof value.aiEnabled !== 'boolean') throw new Error('Chat requires explicit aiEnabled.');
      if (value.aiEnabled) required = [...required, 'model.invoke'];
      else if (!policy.searchOnly) throw new Error('Anonymous chat must explicitly select search-only mode.');
    }
    if (search) {
      const mode = new URL(path, this.options.browserOrigin).searchParams.get('mode');
      if (mode !== null && mode !== 'keyword') required = [...required, 'model.invoke'];
    }
    if (required.some(capability => !this.can(capability))) {
      throw new ApiError(403, 'insufficient_capability', `Unlock Hermes with ${[...new Set(required)].join(', ')} permission`, required);
    }
    const generation = this.state.generation;
    const response = await this.send(path, init, this.state.phase === 'authenticated', generation);
    const flag = /^\/api\/(?:settings|preferences)$/.test(path) ? 'saved'
      : path === '/api/dead-letters/dismiss' ? 'dismissed'
        : path === '/api/sync/reset' ? 'reset'
          : /\/suggestions\/\d+\/approve$/.test(path) ? 'approved'
            : /\/suggestions\/\d+\/reject$/.test(path) ? 'rejected' : undefined;
    if (!['GET', 'HEAD'].includes(method) && flag && !response.headers.get('content-type')?.startsWith('application/json')) {
      throw new Error('The Service did not confirm the action.');
    }
    if (!['GET', 'HEAD'].includes(method) && response.headers.get('content-type')?.startsWith('application/json')) {
      const value: unknown = await response.clone().json();
      this.current(generation);
      if (object(value) && (typeof value.error === 'string' || value.ok === false || value.success === false || value.status === 'failed')) {
        throw new Error(typeof value.error === 'string' ? value.error : 'The Service did not complete the action.');
      }
      if (flag && (!object(value) || value[flag] !== true)) throw new Error('The Service did not confirm the action.');
    }
    return response;
  };
  check = (): Promise<void> => {
    if (this.disposed) return Promise.resolve();
    if (this.checking) return this.checking;
    // Pairing is already instance-bound and expires independently; don't abandon it on focus.
    if (this.state.phase === 'pairing') return Promise.resolve();
    const generation = this.state.generation;
    const checking = this.checkStatus(generation).finally(() => {
      if (this.checking === checking) this.checking = undefined;
    });
    this.checking = checking;
    return checking;
  };
  private async checkStatus(generation: number): Promise<void> {
    let receivedStatus = false;
    try {
      if (this.token && this.clock() >= this.deadline) this.clear('expired', 'Access expired. Reconnect to continue.');
      this.current(generation);
      const response = await this.send('/api/access', {}, Boolean(this.token), generation);
      receivedStatus = true;
      if (response.status !== 200) throw new Error('Invalid access status.');
      const value = await protocolJson(response); this.current(generation);
      exact(value, ['version', 'instanceId', 'processId', 'processStartTimeUtc', 'origin', 'state', 'capabilities', 'expiresAt', 'pairingMethod']);
      if (value.version !== 1 || !canonical(value.instanceId, 16) || !audience(value.origin)
        || !Number.isSafeInteger(value.processId) || Number(value.processId) <= 0 || !timestamp(value.processStartTimeUtc)
        || value.pairingMethod !== 'ownerApproval' || !capabilities(value.capabilities, true)
        || !['anonymous', 'authenticated'].includes(String(value.state))
        || (value.state === 'anonymous' ? value.expiresAt !== null : !timestamp(value.expiresAt))
        || ((this.options.apiAudience ?? this.options.apiOrigin) && value.origin !== (this.options.apiAudience ?? this.options.apiOrigin))) throw new Error('Invalid Service access identity.');
      if (this.state.instanceId && (value.instanceId !== this.state.instanceId || value.origin !== this.state.apiOrigin)) {
        this.native = undefined;
        this.clear('expired', 'The Service restarted or changed. Reconnect and confirm the new instance.');
        this.publish({ instanceId: value.instanceId, apiOrigin: value.origin });
        return;
      }
      this.publish({ instanceId: value.instanceId, apiOrigin: value.origin });
      if (value.state === 'authenticated') {
        if (!this.token || !timestamp(value.expiresAt)) throw new Error('Unexpected authenticated status.');
        this.deadline = Math.min(this.deadline, this.clock() + Math.max(0, Date.parse(value.expiresAt) - this.utc()));
        if (this.clock() >= this.deadline) { this.clear('expired', 'Access expired. Reconnect to continue.'); return; }
        const granted = value.capabilities;
        const lostPermission = this.state.capabilities.some(capability => !granted.includes(capability));
        if (lostPermission) this.invalidate();
        this.publish({ generation: this.state.generation + (lostPermission ? 1 : 0),
          phase: 'authenticated', capabilities: granted, expiresAt: value.expiresAt, message: undefined });
        this.armExpiry();
      } else {
        if (this.token) this.clear('anonymous', 'The Service no longer recognises this session.');
        else this.publish({ phase: this.state.phase === 'expired' ? 'expired' : 'anonymous', capabilities: [], message: this.state.message });
      }
      if (this.waitingNative) {
        const ready = this.waitingNative;
        this.waitingNative = undefined;
        this.acceptNative(ready);
      }
    } catch (error) {
      if (!this.isCurrent(generation) || (error instanceof ApiError && error.status === 401)) return;
      if (this.token && receivedStatus) {
        this.clear('unavailable', 'The Service returned an invalid access status. Reconnect to confirm its identity.');
        return;
      }
      // A transient outage is not a successful logout or an anonymous configuration.
      this.publish({ phase: this.token ? 'authenticated' : 'unavailable',
        message: error instanceof Error ? error.message : 'Service unavailable.' });
      if (this.token) this.armExpiry();
    }
  }
  pair = async (): Promise<void> => {
    if (this.disposed || this.state.phase === 'pairing' || this.state.phase === 'checking') return;
    if (!this.state.instanceId || !this.state.apiOrigin) { await this.check(); return; }
    this.clear('pairing');
    const generation = this.state.generation;
    const instanceId = this.state.instanceId;
    const started = this.clock();
    try {
      const crypto = this.options.crypto ?? globalThis.crypto;
      this.verifier = base64url(crypto.getRandomValues(new Uint8Array(32)));
      const challenge = base64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(this.verifier))));
      this.current(generation);
      const response = await this.send('/api/access/pairings', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version: 1, instanceId, challenge, requestedCapabilities }),
      }, false, generation);
      if (response.status !== 201) throw new Error('The Service did not create a pairing.');
      const value = await protocolJson(response); this.current(generation);
      exact(value, ['version', 'instanceId', 'pairingId', 'userCode', 'browserOrigin', 'apiOrigin', 'requestedCapabilities', 'expiresAt', 'status']);
      if (value.version !== 1 || value.instanceId !== instanceId || !canonical(value.pairingId, 16)
        || typeof value.userCode !== 'string' || !/^[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){2}$/.test(value.userCode)
        || value.browserOrigin !== this.options.browserOrigin || value.apiOrigin !== this.state.apiOrigin
        || !capabilities(value.requestedCapabilities, false) || value.requestedCapabilities.join(',') !== requestedCapabilities.join(',')
        || !timestamp(value.expiresAt) || value.status !== 'pending') throw new Error('Invalid browser pairing binding.');
      this.pairDeadline = Math.min(started + 180_000, this.clock() + Math.max(0, Date.parse(value.expiresAt) - this.utc()));
      this.publish({ pairing: { pairingId: value.pairingId, userCode: value.userCode, expiresAt: value.expiresAt } });
      this.expiryTimer = setTimeout(() => { this.clear('expired', 'Pairing expired. Reconnect for a new code.'); }, Math.max(0, this.pairDeadline - this.clock()));
      this.noticePairing(challenge);
      this.schedulePoll(generation);
    } catch (error) {
      if (this.isCurrent(generation)) this.clear('unavailable', error instanceof Error ? error.message : 'Pairing failed. Reconnect.');
    }
  };
  private noticePairing(challenge: string): void {
    const { pairing, instanceId } = this.state;
    if (!pairing || !this.native || this.native.instanceId !== instanceId) return;
    this.options.postNative?.({ version: 1, type: 'hermes.access.pairing', navigationId: this.native.navigationId,
      instanceId, pairingId: pairing.pairingId, userCode: pairing.userCode, challenge });
  }
  private schedulePoll(generation: number): void {
    clearTimeout(this.pollTimer);
    if (this.clock() >= this.pairDeadline) { this.clear('expired', 'Pairing expired. Reconnect for a new code.'); return; }
    this.pollTimer = setTimeout(() => { void this.exchange(generation); }, Math.min(2000, this.pairDeadline - this.clock()));
  }
  private async exchange(generation: number): Promise<void> {
    if (!this.isCurrent(generation)) return;
    if (this.clock() >= this.pairDeadline) { this.clear('expired', 'Pairing expired. Reconnect for a new code.'); return; }
    try {
      const response = await this.send(`/api/access/pairings/${this.state.pairing?.pairingId}/exchange`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version: 1, instanceId: this.state.instanceId, verifier: this.verifier }),
      }, false, generation);
      const value = await protocolJson(response); this.current(generation);
      if (response.status === 202) {
        exact(value, ['version', 'instanceId', 'status', 'expiresAt']);
        if (value.version !== 1 || value.instanceId !== this.state.instanceId || value.status !== 'pending' || !timestamp(value.expiresAt)) {
          throw new Error('Invalid pending pairing response.');
        }
        this.pairDeadline = Math.min(this.pairDeadline, this.clock() + Math.max(0, Date.parse(value.expiresAt) - this.utc()));
        this.schedulePoll(generation); return;
      }
      exact(value, ['version', 'instanceId', 'tokenType', 'accessToken', 'capabilities', 'expiresAt']);
      if (response.status !== 201 || value.version !== 1 || value.instanceId !== this.state.instanceId
        || value.tokenType !== 'Bearer' || typeof value.accessToken !== 'string'
        || !value.accessToken.startsWith(`h1.${this.state.instanceId}.`) || !canonical(value.accessToken.split('.')[2], 32)
        || value.accessToken.split('.').length !== 3 || !capabilities(value.capabilities, false) || !timestamp(value.expiresAt)) {
        throw new Error('Invalid access exchange response.');
      }
      this.token = value.accessToken; this.verifier = undefined;
      clearTimeout(this.expiryTimer);
      this.deadline = this.clock() + Math.min(1_800_000, Math.max(0, Date.parse(value.expiresAt) - this.utc()));
      if (this.clock() >= this.deadline) { this.clear('expired', 'Access already expired. Reconnect.'); return; }
      this.publish({ phase: 'authenticated', pairing: undefined, capabilities: ['archive.read', ...value.capabilities], expiresAt: value.expiresAt, message: undefined });
      this.armExpiry();
      if (this.native && this.native.instanceId === this.state.instanceId) {
        this.options.postNative?.({ version: 1, type: 'hermes.access.status', navigationId: this.native.navigationId,
          instanceId: this.state.instanceId, state: 'authenticated', capabilities: this.state.capabilities, expiresAt: value.expiresAt });
      }
    } catch (error) {
      if (!this.isCurrent(generation)) return;
      // Never retry an exchange whose successful reply may have been lost.
      this.clear(error instanceof ApiError && error.status === 410 ? 'expired' : 'unavailable',
        error instanceof Error ? error.message : 'Pairing failed. Reconnect for a new code.');
    }
  }
  private armExpiry(): void {
    clearTimeout(this.expiryTimer);
    this.expiryTimer = setTimeout(() => { this.clear('expired', 'Access expired. Reconnect to continue.'); }, Math.max(0, this.deadline - this.clock()));
  }
  acceptNative = (value: unknown): void => {
    if (this.disposed) return;
    try {
      exact(value, ['version', 'type', 'navigationId', 'instanceId']);
      if (value.version !== 1 || value.type !== 'hermes.access.ready' || !canonical(value.navigationId, 16)
        || !canonical(value.instanceId, 16)
        || this.rejectedNavigations.has(value.navigationId) || value.navigationId === this.native?.navigationId) return;
      if (!this.state.instanceId && this.state.phase === 'checking') { this.waitingNative = value as NativeReady; return; }
      if (value.instanceId !== this.state.instanceId) return;
      if (this.native) {
        this.rejectedNavigations.add(this.native.navigationId);
        if (this.rejectedNavigations.size > 32) { this.native = undefined; this.clear('unavailable', 'Native navigation limit reached. Reload the page.'); return; }
        this.clear('anonymous', 'Native navigation changed. Reconnect.');
      } else if (this.state.phase === 'pairing' || this.state.phase === 'authenticated') this.clear('anonymous', 'Reconnect using the current native navigation.');
      this.native = value as NativeReady;
      void this.pair();
    } catch { /* Unrelated or malformed host messages cannot grant access. */ }
  };
  cancel = (): void => { this.clear('anonymous', 'Pairing cancelled. No authority was granted.'); };
  lock = async (): Promise<void> => {
    const credential = this.token;
    this.clear('anonymous', credential ? 'Locked locally. Confirming server revocation…' : 'Locked.');
    const generation = this.state.generation;
    if (!credential) return;
    try {
      const response = await this.send('/api/access/session', { method: 'DELETE' }, true, generation, credential, false);
      if (response.status !== 204) throw new Error('The Service did not revoke the session.');
      if (this.isCurrent(generation)) this.publish({ message: 'Locked.' });
    } catch (error) {
      if (this.isCurrent(generation)) this.publish({ message: `Locked locally. Server revocation was not confirmed: ${error instanceof Error ? error.message : 'Service unavailable.'}` });
    }
  };
  dispose = (): void => {
    this.clear('anonymous'); this.native = undefined; this.waitingNative = undefined; this.rejectedNavigations.clear();
    this.listeners.clear(); this.disposed = true;
  };
}
