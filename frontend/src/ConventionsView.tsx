import { useState } from 'react';
import type { Conventions } from './system';
import { Icon } from './Icon';

// Пример файла свода — показываем, как дополнять (копипаст-стартер).
const SAMPLE = `{
  "markers":  { "IRequestHandler": "Handler" },
  "fallback": { "IRepository": "Repository" },
  "expect":   [ { "suffix": "Command", "marker": "ICommand", "role": "Command" } ]
}`;

/** Модалка: применённый свод (маркеры + fallback + правила) + подсказка, как дополнить файлом. */
export function ConventionsView({ conventions, onClose }: { conventions: Conventions; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(SAMPLE).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); }).catch(() => {});
  };

  return (
    <div className="cv-backdrop" onClick={onClose}>
      <div className="cv" onClick={(e) => e.stopPropagation()}>
        <div className="cv__head">
          <div className="cv__title"><Icon name="box" size={16} /> Свод конвенций</div>
          <button className="cv__close" onClick={onClose} title="Закрыть"><Icon name="close" size={16} /></button>
        </div>
        <div className="cv__src">{conventions.source}</div>

        <div className="cv__body">
          <div className="cv__hint">
            Свод задаёт, как по коду распознавать роли типов и что считать «расхождением со сводом».
            Положи <code>archforge.conventions.json</code> в корень проекта — он дополнит/переопределит встроенный.
          </div>

          <Section title="МАРКЕРЫ" hint="база/интерфейс → роль (уверенно)" entries={conventions.markers.map((m) => [m.name, m.role])} />
          <Section title="FALLBACK" hint="то же, но эвристика" entries={conventions.fallback.map((m) => [m.name, m.role])} />

          <div className="cv__section">
            <div className="cv__sec-head">ПРАВИЛА (дрейф) <span>суффикс имени → ожидаемый маркер</span></div>
            {conventions.rules.length === 0 ? <div className="cv__empty">—</div> : (
              <ul className="cv__list">
                {conventions.rules.map((r) => (
                  <li key={r.suffix} className="cv__rule">
                    <code>*{r.suffix}</code> ожидает <code>{r.marker}</code> <span className="cv__role">→ {r.role}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>

          <div className="cv__section">
            <div className="cv__sec-head">КАК ДОПОЛНИТЬ <button className="cv__copy" onClick={copy}>{copied ? 'скопировано ✓' : 'копировать пример'}</button></div>
            <pre className="cv__sample">{SAMPLE}</pre>
          </div>
        </div>
      </div>
    </div>
  );
}

function Section({ title, hint, entries }: { title: string; hint: string; entries: [string, string][] }) {
  return (
    <div className="cv__section">
      <div className="cv__sec-head">{title} <span>{hint}</span></div>
      {entries.length === 0 ? <div className="cv__empty">—</div> : (
        <ul className="cv__list">
          {entries.map(([name, role]) => (
            <li key={name} className="cv__entry"><code>{name}</code> <span className="cv__role">→ {role}</span></li>
          ))}
        </ul>
      )}
    </div>
  );
}
