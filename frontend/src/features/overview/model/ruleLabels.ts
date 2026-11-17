// Человеческие подписи видов находок (Problem.kind). Незнакомый вид — null: экран покажет сырой kind.

const LABELS: Record<string, string> = {
  'shared-db': 'Общая база данных',
  'shared-store': 'Общее хранилище',
  'service-cycle': 'Цикл между сервисами',
  'dangling-event': 'Событие без получателя',
  'layer-violation': 'Нарушение слоёв',
  drift: 'Расхождение со сводом',
  'unknown-role': 'Роль типа не определена',
  'exposed-datastore': 'Хранилище открыто наружу',
  'di-missing': 'Не зарегистрирована зависимость DI',
  'unattributed-calls': 'Вызовы без узла системы',
  'sync-fan-out': 'Синхронный веер вызовов',
  'sync-chain': 'Длинная цепочка синхронных вызовов',
  'redundant-null-check': 'Лишняя проверка на null',
  'folder-structure': 'Раскладка папок не по сущности',
};

export const ruleLabel = (kind: string): string | null => (Object.hasOwn(LABELS, kind) ? LABELS[kind] : null);
