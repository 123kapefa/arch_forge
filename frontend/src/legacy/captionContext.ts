import { createContext } from 'react';

/** Действие «свернуть/развернуть группу одиночек» — подпись-узел на холсте вызывает его через контекст (данные узла без колбэков). */
export const CaptionToggleContext = createContext<(groupKey: string, collapsed: boolean) => void>(() => {});
