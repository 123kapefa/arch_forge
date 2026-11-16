import type { RouteObject } from 'react-router';
import { Shell } from './Shell';
import { NotFound } from './NotFound';
import { RouteError } from './RouteError';

// Каждый экран грузится отдельным чанком; карта тянет за собой legacy (react-flow, elk) только по /map.
const screen = (load: () => Promise<{ [k: string]: React.ComponentType }>, name: string): RouteObject['lazy'] =>
  async () => ({ Component: (await load())[name] });

export const routes: RouteObject[] = [
  {
    path: '/',
    Component: Shell,
    ErrorBoundary: RouteError,
    children: [
      { index: true, lazy: screen(() => import('../features/start/StartScreen'), 'StartScreen') },
      { path: 'overview', lazy: screen(() => import('../features/overview/OverviewScreen'), 'OverviewScreen') },
      { path: 'problems/:id', lazy: screen(() => import('../features/problem/ProblemScreen'), 'ProblemScreen') },
      { path: 'impact/:node', lazy: screen(() => import('../features/impact/ImpactScreen'), 'ImpactScreen') },
      { path: 'map', lazy: screen(() => import('../features/map/MapScreen'), 'MapScreen') },
      { path: 'slices', lazy: screen(() => import('../features/slices/SlicesScreen'), 'SlicesScreen') },
      { path: 'diff', lazy: screen(() => import('../features/diff/DiffScreen'), 'DiffScreen') },
      { path: 'report', lazy: screen(() => import('../features/report/ReportScreen'), 'ReportScreen') },
      { path: 'states', lazy: screen(() => import('../features/states/StatesScreen'), 'StatesScreen') },
      { path: '*', Component: NotFound },
    ],
  },
];
