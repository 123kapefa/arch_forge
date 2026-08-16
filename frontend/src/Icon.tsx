// Единый набор линейных иконок (24×24, stroke=currentColor) — вместо эмодзи в UI.
// Несколько подпутей в одном d (разделены M) рисуются как отдельные штрихи.

const PATHS = {
  refresh: 'M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 '
    + 'M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5',
  save: 'M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z '
    + 'M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7 M7 3v4a1 1 0 0 0 1 1h7',
  reset: 'M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5',
  download: 'M12 15V3 M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5',
  close: 'M18 6 6 18 M6 6l12 12',
  folder: 'M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z',
  folderOpen: 'm6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2',
  drive: 'M22 12H2 M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z M6 16h.01 M10 16h.01',
  chevronDown: 'M6 9l6 6 6-6',
  chevronUp: 'M18 15l-6-6-6 6',
  chevronLeft: 'M15 18l-6-6 6-6',
  chevronRight: 'M9 18l6-6-6-6',
  arrowUp: 'M12 19V5 M5 12l7-7 7 7',
  branch: 'M6 3v12 M15 6a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M3 18a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M18 9a9 9 0 0 1-9 9',
  // расширить панель влево (на всю ширину): двунаправленная стрелка с упором справа
  expandLeft: 'M20 4v16 M16 12H4 M8 8l-4 4 4 4',
  // вернуть ширину: стрелка внутрь к правому упору
  shrinkRight: 'M20 4v16 M5 12h11 M12 8l4 4-4 4',
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({ name, size = 16 }: { name: IconName; size?: number }) {
  return (
    <svg
      className="icon" width={size} height={size} viewBox="0 0 24 24" fill="none"
      stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={PATHS[name]} />
    </svg>
  );
}
