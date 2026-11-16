import type { ReactNode } from 'react';

/** Что именно выбрано — метка в шапке единого инспектора (R18). */
export type InspectorWhat = 'УЗЕЛ' | 'СЛОЙ' | 'ТИП' | 'СВЯЗЬ' | 'НАХОДКА' | 'СЕРВИС' | 'ПОТОК';

/**
 * Единая структура инспектора: метка «что это» + заголовок + подзаголовок + секции (children).
 * Содержимое секций у каждого вида своё и сохраняется как было — рамка одна для всех.
 */
export function InspectorFrame({ what, title, subtitle, dot, mono, className, children }: {
  what: InspectorWhat;
  title: ReactNode;
  subtitle?: ReactNode;
  dot?: string;
  mono?: boolean;
  className?: string;
  children?: ReactNode;
}) {
  return (
    <div className={`uinsp ${className ?? ''}`.trim()} data-what={what}>
      <div className="uinsp__what">{what}</div>
      <div className="uinsp__head">
        {dot && <span className="uinsp__dot" style={{ background: dot }} />}
        <span className={`uinsp__title${mono ? ' uinsp__title--mono' : ''}`}>{title}</span>
      </div>
      {subtitle ? <div className="uinsp__sub">{subtitle}</div> : null}
      {children}
    </div>
  );
}

/** Единое пустое состояние: ничего не выбрано. */
export function InspectorEmpty() {
  return (
    <div className="uinsp uinsp--empty" data-what="empty">
      <div className="uinsp__what">НИЧЕГО НЕ ВЫБРАНО</div>
      <div className="uinsp__hint">Кликните узел, связь между узлами или факт в списке проблем.</div>
      <ul className="uinsp__tips">
        <li>узел → слои, проблемы, связи</li>
        <li>связь → что по ней идёт</li>
        <li>двойной клик по узлу → внутрь</li>
      </ul>
    </div>
  );
}
