import { describe, expect, it } from 'vitest';
import { flowEdgeKeys, flowNodeIds, stepEdgeKey, type Flow, type FlowStep } from './flow';

const step = (o: Partial<FlowStep>): FlowStep => ({ index: 0, depth: 1, from: 'a', to: 'b', toName: 'b', kind: 'http', label: 'GET /x', status: 'ok', ...o });

describe('flow', () => {
  it('ключи рёбер совпадают с ключами холста (source|target|kind)', () => {
    expect(stepEdgeKey(step({}))).toBe('a|b|http-call');
    expect(stepEdgeKey(step({ kind: 'publish', to: 'bus' }))).toBe('a|bus|publishes');
    // подписка на карте идёт подписчик → шина, шаг потока — шина → подписчик
    expect(stepEdgeKey(step({ kind: 'consume', from: 'bus', to: 'mail' }))).toBe('mail|bus|subscribes');
    expect(stepEdgeKey(step({ kind: 'data', to: 'pg' }))).toBe('a|pg|rw-db');
    expect(stepEdgeKey(step({ kind: 'weird' }))).toBeNull();
  });

  it('собирает рёбра и узлы потока; без потока — null', () => {
    const f: Flow = { entry: 'a', entryName: 'a', truncated: false, limits: { maxDepth: 6, maxSteps: 150 }, notes: [],
      steps: [step({}), step({ index: 1, from: 'b', to: 'c', toName: 'c' })] };
    expect([...flowEdgeKeys(f)!]).toEqual(['a|b|http-call', 'b|c|http-call']);
    expect([...flowNodeIds(f)!].sort()).toEqual(['a', 'b', 'c']);
    expect(flowEdgeKeys(null)).toBeNull();
  });
});
