import { createContext } from 'react';

// Режим «Deploy» (E3.2): какие оверлеи показывать на плашках и ярус старта по узлам. null → обычная «Структура».
export interface DeployOverlays { ports: boolean; networks: boolean; healthcheck: boolean; start: boolean }
export interface DeployInfo { overlays: DeployOverlays; tiers: ReadonlyMap<string, number> }

export const DeployContext = createContext<DeployInfo | null>(null);
