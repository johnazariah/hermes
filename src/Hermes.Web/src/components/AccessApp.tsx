import App from '../App';
import { useAccess } from '../hooks/useAccess';

export function AccessApp() {
  const state = useAccess();
  // Ordinary focus checks keep this fence stable so editors stay mounted.
  return <App key={state.generation} />;
}
