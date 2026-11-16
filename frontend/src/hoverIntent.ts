// «Намерение наведения»: подсветка включается, только если курсор задержался на элементе, и выключается с небольшой
// задержкой — пролёт через элементы и переход между соседними не мигает. Чистая логика (без React), таймеры — стандартные.

export interface HoverTarget { kind: 'node' | 'edge'; id: string }

export interface HoverIntentOptions {
  onChange: (t: HoverTarget | null) => void;
  enterDelay?: number;  // мс: сколько курсор должен задержаться, чтобы включить подсветку
  leaveDelay?: number;  // мс: «льготное» время до выключения
}

export interface HoverIntent {
  enter(t: HoverTarget): void;
  leave(): void;
  /** Начался drag/pan/zoom: сбросить подсветку сразу и игнорировать наведение до resume(). */
  suspend(): void;
  resume(): void;
  dispose(): void;
}

export const ENTER_DELAY = 150;
export const LEAVE_DELAY = 100;

export function createHoverIntent({ onChange, enterDelay = ENTER_DELAY, leaveDelay = LEAVE_DELAY }: HoverIntentOptions): HoverIntent {
  let active: HoverTarget | null = null;
  let enterTimer: ReturnType<typeof setTimeout> | null = null;
  let leaveTimer: ReturnType<typeof setTimeout> | null = null;
  let suspended = false;
  const same = (a: HoverTarget | null, b: HoverTarget | null) => a === b || (!!a && !!b && a.kind === b.kind && a.id === b.id);
  const clearEnter = () => { if (enterTimer) { clearTimeout(enterTimer); enterTimer = null; } };
  const clearLeave = () => { if (leaveTimer) { clearTimeout(leaveTimer); leaveTimer = null; } };
  const set = (t: HoverTarget | null) => { if (!same(active, t)) { active = t; onChange(t); } };

  return {
    enter(t) {
      if (suspended) return;
      clearLeave();                       // вернулись на тот же/соседний элемент — выключение отменяем
      if (same(active, t)) { clearEnter(); return; }
      clearEnter();
      enterTimer = setTimeout(() => { enterTimer = null; set(t); }, enterDelay); // активный (если есть) держится до переключения
    },
    leave() {
      clearEnter();                       // не успели задержаться — ничего не включаем
      if (!active) return;
      clearLeave();
      leaveTimer = setTimeout(() => { leaveTimer = null; set(null); }, leaveDelay);
    },
    suspend() { suspended = true; clearEnter(); clearLeave(); set(null); },
    resume() { suspended = false; },
    dispose() { clearEnter(); clearLeave(); },
  };
}
