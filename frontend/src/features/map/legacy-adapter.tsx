// ЕДИНСТВЕННОЕ место нового кода, где разрешён импорт из legacy/ (проверяет scripts/check-boundaries.mjs).
// Монтирует старую карту как есть; её глобальные стили ограничены корнем .legacy-root.
import '../../legacy/index.css';
import { SystemView } from '../../legacy/SystemView';

export function LegacyMap() {
  return (
    <div className="legacy-root">
      <SystemView />
    </div>
  );
}
