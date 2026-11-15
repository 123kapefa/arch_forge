import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { InspectorEmpty, InspectorFrame } from './InspectorFrame';
import { EdgeInspector } from './EdgeInspector';

describe('единый инспектор (R18)', () => {
  it('рамка: метка «что это», заголовок, подзаголовок, секции', () => {
    const html = renderToStaticMarkup(<InspectorFrame what="УЗЕЛ" title="orders" subtitle="сервис"><p>секция</p></InspectorFrame>);
    expect(html).toContain('data-what="УЗЕЛ"');
    expect(html).toContain('orders');
    expect(html).toContain('сервис');
    expect(html).toContain('секция');
  });
  it('пустое состояние единое', () => {
    expect(renderToStaticMarkup(<InspectorEmpty />)).toContain('НИЧЕГО НЕ ВЫБРАНО');
  });
  it('инспектор связи: метка СВЯЗЬ, имена узлов — ссылки при onFocus', () => {
    const edge = { id: 'e', source: 'a', target: 'b', kind: 'rw-db', confidence: 0.9, violation: false };
    const html = renderToStaticMarkup(<EdgeInspector edge={edge} names={{ a: 'api', b: 'pg' }} scanRoot="" callsRequested={false} onFocus={() => {}} />);
    expect(html).toContain('data-what="СВЯЗЬ"');
    expect(html).toContain('uinsp__link');
    expect(html).toContain('доступ к БД');
    const plain = renderToStaticMarkup(<EdgeInspector edge={edge} names={{ a: 'api', b: 'pg' }} scanRoot="" callsRequested={false} />);
    expect(plain).not.toContain('uinsp__link');
  });
});
