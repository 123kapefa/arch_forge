import { createContext } from 'react';
import type { Lens } from './systemModel';

// Активная линза + число её проблем на каждом узле — чтобы узлы L0 красились по выбранной линзе (#70).
// hits: id узла → сколько проблем активной линзы «попадает» в узел (арх — локализованные; дрейф — по владельцу файла).
export interface LensInfo {
  lens: Lens;
  hits: ReadonlyMap<string, number>;
}

export const LensContext = createContext<LensInfo>({ lens: 'architecture', hits: new Map() });
