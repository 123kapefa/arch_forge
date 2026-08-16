import { createContext } from 'react';

// «Раскладку трогали» (перетаскивали узлы): true → рёбра идут на живой обход по текущим позициям,
// а не по статичным маршрутам ELK (иначе перемещённый блок мог бы въехать в чужое ребро).
export const LayoutDirtyContext = createContext(false);
