export interface AccessDiagnostic {
  requests: number;
  responses: number;
  finished: number;
  failed: number;
  status: number;
  requestMs: number;
  headersMs: number;
  bodyMs: number;
}

export function accessDiagnostic(value: AccessDiagnostic): string {
  const fields: readonly [keyof AccessDiagnostic, number][] = [
    ['requests', 999], ['responses', 999], ['finished', 999], ['failed', 999],
    ['status', 599], ['requestMs', 999_999], ['headersMs', 999_999], ['bodyMs', 999_999],
  ];
  return 'HERMES_ACCESS_BOOTSTRAP v=1 ' + fields.map(([key, maximum]) => {
    const raw = value[key];
    const invalidStatus = key === 'status'
      && (!Number.isInteger(raw) || raw > maximum || (raw !== 0 && raw < 100));
    const number = raw === -1 ? -1
      : !Number.isFinite(raw) || raw < 0 || invalidStatus ? -2 : Math.min(maximum, Math.ceil(raw));
    return `${key}=${number}`;
  }).join(' ');
}
