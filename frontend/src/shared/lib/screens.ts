/** Реестр экранов для навигации: путь маршрута, пример адреса (для параметрических), название. */
export interface ScreenLink {
  path: string;
  example: string;
  title: string;
  /** Показывать в верхней панели. */
  nav: boolean;
}

export const SCREENS: readonly ScreenLink[] = [
  { path: '/', example: '/', title: 'Старт', nav: true },
  { path: '/overview', example: '/overview', title: 'Обзор', nav: true },
  { path: '/problems/:id', example: '/problems/example', title: 'Проблема', nav: false },
  { path: '/impact/:node', example: '/impact/example', title: 'Влияние', nav: false },
  { path: '/map', example: '/map', title: 'Карта', nav: true },
  { path: '/slices', example: '/slices', title: 'Срезы', nav: true },
  { path: '/diff', example: '/diff', title: 'Дифф', nav: true },
  { path: '/report', example: '/report', title: 'Отчёт', nav: true },
  { path: '/states', example: '/states', title: 'Состояния', nav: true },
];
