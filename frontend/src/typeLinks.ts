// Модель экрана типов слоя (L2): связи ВНЕ слоя, деление «одиночек» и роль «вход для фреймворка».
// Чистые функции — без React/ELK, чтобы их можно было тестировать. Правда про «изолированный» тип: у него нет
// связей ВНУТРИ слоя, но могут быть связи с другими слоями (они не рисуются на карте, но не теряются).

import type { GraphDto } from './api';

/** Одна связь типа с типом другого слоя (свёрнутая по паре «сосед + направление»). */
export interface OutsideLink {
  peerId: string;
  peerName: string;
  peerLayer: string;
  dir: 'out' | 'in';      // out — этот тип ссылается на соседа; in — сосед ссылается на этот тип
  kinds: string[];        // виды рёбер между ними (implements | holds | uses | refs | …)
  weak: boolean;          // все рёбра слабые (refs и неизвестные виды) — рисуем нейтрально
}

export interface OutsideSummary {
  links: OutsideLink[];
  out: number;
  in: number;
  layers: string[];       // слои, с которыми есть связи (по алфавиту)
}

// Известные «сильные» виды рёбер; всё остальное (refs и будущие виды) считается слабым и показывается нейтрально.
const STRONG_KINDS = new Set(['implements', 'holds', 'uses', 'inherits', 'extends', 'raises']);

const layerOf = (l: string | undefined) => (l && l.trim() ? l : 'unknown');

/** Связи каждого типа слоя с типами ДРУГИХ слоёв (ключ — id типа слоя). Типы без внешних связей в карту не попадают. */
export function outsideLinksByType(map: GraphDto, layer: string): Map<string, OutsideSummary> {
  const byId = new Map(map.nodes.map((n) => [n.id, n] as const));
  const raw = new Map<string, Map<string, { link: OutsideLink; kinds: Set<string> }>>();
  const add = (ownId: string, peerId: string, dir: 'out' | 'in', kind: string) => {
    const peer = byId.get(peerId);
    if (!peer) return;
    let per = raw.get(ownId);
    if (!per) raw.set(ownId, (per = new Map()));
    const key = `${dir}|${peerId}`;
    let rec = per.get(key);
    if (!rec) {
      rec = { link: { peerId, peerName: peer.name, peerLayer: layerOf(peer.layer), dir, kinds: [], weak: true }, kinds: new Set() };
      per.set(key, rec);
    }
    rec.kinds.add(kind);
  };
  for (const e of map.edges) {
    const s = byId.get(e.source), t = byId.get(e.target);
    if (!s || !t) continue;
    const sIn = s.layer === layer, tIn = t.layer === layer;
    if (sIn === tIn) continue; // обе внутри (внутрислойное) или обе снаружи (не про этот слой)
    if (sIn) add(s.id, t.id, 'out', e.kind);
    else add(t.id, s.id, 'in', e.kind);
  }
  const res = new Map<string, OutsideSummary>();
  for (const [id, per] of raw) {
    const links = [...per.values()].map(({ link, kinds }) => ({
      ...link,
      kinds: [...kinds].sort(),
      weak: ![...kinds].some((k) => STRONG_KINDS.has(k)),
    }));
    links.sort((a, b) => a.peerLayer.localeCompare(b.peerLayer) || a.peerName.localeCompare(b.peerName) || a.dir.localeCompare(b.dir));
    res.set(id, {
      links,
      out: links.filter((l) => l.dir === 'out').length,
      in: links.filter((l) => l.dir === 'in').length,
      layers: [...new Set(links.map((l) => l.peerLayer))].sort(),
    });
  }
  return res;
}

/** Короткая подпись карточки: «вне слоя: 3 (→ Domain, ← Api)». Пусто → null. */
export function outsideBadge(s: OutsideSummary | undefined): { text: string; title: string } | null {
  if (!s || s.links.length === 0) return null;
  const arrows = [s.out > 0 ? `→ ${s.out}` : '', s.in > 0 ? `← ${s.in}` : ''].filter(Boolean).join(' ');
  const text = `вне слоя: ${s.links.length}`;
  const title = `связи с другими слоями (${s.layers.join(', ')}): ${arrows} — список в инспекторе`;
  return { text, title };
}

export type IsoGroup = 'outsideOnly' | 'none';

export const ISO_LABEL: Record<IsoGroup, string> = {
  outsideOnly: 'Связи только вне слоя',
  none: 'Вообще без связей',
};

/** Куда относить «одиночку» (нет рёбер внутри слоя): есть внешние связи → outsideOnly, иначе none. */
export function isolatedGroup(summary: OutsideSummary | undefined): IsoGroup {
  return summary && summary.links.length > 0 ? 'outsideOnly' : 'none';
}

// Роли узла (`role` из /api/map) → подпись «вход для фреймворка». Это типы, которые никто не вызывает напрямую:
// их находит и запускает сам фреймворк (EF, DI, ASP.NET, хостинг), поэтому «нет связей» не значит «не нужен».
const ROLE_ENTRY: Record<string, string> = {
  'EF migration': 'EF-миграция',
  'EF snapshot': 'снимок модели EF',
  'EF factory': 'фабрика DbContext для EF (design-time)',
  'EF config': 'конфигурация EF (подхватывается ApplyConfigurations)',
  'DI/Extensions': 'DI-расширение (вызывается при старте)',
  Validator: 'валидатор (подхватывается регистрацией валидаторов)',
  Endpoint: 'endpoint (подхватывается маршрутизацией)',
  Controller: 'контроллер (вызывает маршрутизация ASP.NET)',
  'Hosted service': 'фоновый сервис (запускает хост)',
  'OIDC handler': 'обработчик OpenIddict (регистрируется в DI)',
  'Razor Page': 'Razor-страница (вызывает маршрутизация)',
  DbContext: 'DbContext (создаёт DI/EF)',
};

/**
 * Подпись «вход для фреймворка» или null. Сначала по роли узла (бэкенд уже её определил), затем — по имени
 * как слабый признак (помечаем «по имени»): `DependencyInjection*`, `*Registration`, `*Extensions`.
 */
export function frameworkEntry(role: string | undefined, name: string): string | null {
  if (role && ROLE_ENTRY[role]) return ROLE_ENTRY[role];
  if (/^DependencyInjection/.test(name) || /Registration$/.test(name) || /ServiceCollectionExtensions$/.test(name)) {
    return 'DI-регистрация (по имени; вызывается при старте)';
  }
  return null;
}
