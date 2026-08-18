import { useState } from 'react';
import App from './App';
import { SystemView } from './SystemView';

type Mode = 'phase1' | 'system';

/** Корень: переключение между тип-графом Фазы 1 (App) и системным обзором L0 (SystemView). */
export function Root() {
  const [mode, setMode] = useState<Mode>('system');

  if (mode === 'system') return <SystemView onExit={() => setMode('phase1')} />;

  return (
    <>
      <button
        onClick={() => setMode('system')}
        style={{
          position: 'fixed', top: 8, right: 8, zIndex: 10,
          background: '#12161d', color: '#c9d1d9', border: '1px solid #263041',
          borderRadius: 6, padding: '4px 10px', cursor: 'pointer',
        }}
      >
        система (Ф1.5) →
      </button>
      <App />
    </>
  );
}
