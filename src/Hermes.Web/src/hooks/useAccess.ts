import { useSyncExternalStore } from 'react';
import { access } from '../api/http';
import type { Capability } from '../api/access';

export function useAccess() {
  const state = useSyncExternalStore(access.subscribe, access.getSnapshot);
  return {
    ...state,
    can: (capability: Capability) => state.phase === 'authenticated' && state.capabilities.includes(capability),
  };
}
