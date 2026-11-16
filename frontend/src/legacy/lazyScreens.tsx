import { lazy, Suspense, useEffect, useState, type ComponentProps } from 'react';

// Тяжёлые экраны/режимы грузятся отдельными чанками при первом открытии (#57).
// Обёртки со Suspense внутри — чтобы SystemView использовал их как обычные компоненты.

/** Индикатор показывается не сразу: быстрая подгрузка чанка не даёт мигания «Загрузка…». */
function DelayedFallback() {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    const t = setTimeout(() => setShown(true), 200);
    return () => clearTimeout(t);
  }, []);
  return shown ? <div className="lazy-fallback" style={{ padding: 24, opacity: 0.6 }}>Загрузка…</div> : null;
}

const CompareViewImpl = lazy(() => import('./CompareView').then((m) => ({ default: m.CompareView })));
const ImportsScreenImpl = lazy(() => import('./ImportsScreen').then((m) => ({ default: m.ImportsScreen })));
const ConventionsViewImpl = lazy(() => import('./ConventionsView').then((m) => ({ default: m.ConventionsView })));
const ProfileViewImpl = lazy(() => import('./ProfileView').then((m) => ({ default: m.ProfileView })));
const FolderPickerImpl = lazy(() => import('./FolderPicker').then((m) => ({ default: m.FolderPicker })));

export function CompareView(p: ComponentProps<typeof CompareViewImpl>) {
  return <Suspense fallback={<DelayedFallback />}><CompareViewImpl {...p} /></Suspense>;
}
export function ImportsScreen(p: ComponentProps<typeof ImportsScreenImpl>) {
  return <Suspense fallback={<DelayedFallback />}><ImportsScreenImpl {...p} /></Suspense>;
}
// Модальные окна: пока чанк грузится, ничего не рисуем (подгрузка — доли секунды).
export function ConventionsView(p: ComponentProps<typeof ConventionsViewImpl>) {
  return <Suspense fallback={null}><ConventionsViewImpl {...p} /></Suspense>;
}
export function ProfileView(p: ComponentProps<typeof ProfileViewImpl>) {
  return <Suspense fallback={null}><ProfileViewImpl {...p} /></Suspense>;
}
export function FolderPicker(p: ComponentProps<typeof FolderPickerImpl>) {
  return <Suspense fallback={null}><FolderPickerImpl {...p} /></Suspense>;
}
