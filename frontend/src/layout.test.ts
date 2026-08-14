import { describe, it, expect } from 'vitest';
import type { Node, Edge } from '@xyflow/react';
import { layout } from './layout';

const mkNode = (id: string, layer = 'Domain'): Node => ({
  id,
  type: 'type',
  position: { x: 0, y: 0 },
  data: { name: id, kind: 'class', role: 'X', layer, via: 'marker', drift: [] },
});
const mkEdge = (s: string, t: string): Edge => ({ id: `${s}-${t}`, source: s, target: t });
const allFinite = (nodes: Node[]) =>
  nodes.every((n) => Number.isFinite(n.position.x) && Number.isFinite(n.position.y));

describe('layout', () => {
  it('пустой граф → пусто, без падений', () => {
    expect(layout([], [])).toEqual([]);
  });

  it('все узлы одиночки (нет рёбер) → все размещены, координаты конечны', () => {
    const nodes = ['a', 'b', 'c', 'd', 'e'].map((id) => mkNode(id));
    const out = layout(nodes, []);
    expect(out).toHaveLength(5);
    expect(allFinite(out)).toBe(true);
  });

  it('связный + одиночки → все узлы на месте, id сохранены, координаты конечны', () => {
    const nodes = ['a', 'b', 'c', 'x', 'y'].map((id) => mkNode(id));
    const out = layout(nodes, [mkEdge('a', 'b'), mkEdge('b', 'c')]);
    expect(out).toHaveLength(5);
    expect(allFinite(out)).toBe(true);
    expect(new Set(out.map((n) => n.id))).toEqual(new Set(['a', 'b', 'c', 'x', 'y']));
  });

  it('одиночки уходят ниже связного подграфа', () => {
    const out = layout(['a', 'b', 'iso'].map((id) => mkNode(id)), [mkEdge('a', 'b')]);
    const iso = out.find((n) => n.id === 'iso')!;
    const connectedMaxY = Math.max(...out.filter((n) => n.id !== 'iso').map((n) => n.position.y));
    expect(iso.position.y).toBeGreaterThan(connectedMaxY);
  });

  it('LR по умолчанию → source справа, target слева', () => {
    const out = layout(['a', 'b'].map((id) => mkNode(id)), [mkEdge('a', 'b')]);
    expect(out[0].sourcePosition).toBe('right');
    expect(out[0].targetPosition).toBe('left');
  });
});
