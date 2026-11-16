// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderApp } from '../../test/renderApp';
import type { SystemGraph } from '../../shared/api';

vi.mock('../map/legacy-adapter', () => ({ LegacyMap: () => <div>legacy-map-stub</div> }));

const base: SystemGraph = {
  project: 'demo',
  nodes: [
    { id: 's1', name: 'auth', kind: 'service', health: 'warning' },
    { id: 'd1', name: 'postgres', kind: 'datastore', health: 'critical' },
  ],
  edges: [{ id: 'e1', source: 's1', target: 'd1', kind: 'rw-db' }],
  coverage: { marker: 1, heuristic: 5, unknown: 4, total: 10 },
  drift: [],
  problems: [
    { id: 'p-info', severity: 'info', kind: 'redundant', title: 'Инфо-находка', location: 's1' },
    { id: 'p/crit', severity: 'critical', kind: 'shared-db', title: 'Критичная находка', location: 'd1', confidence: 0.9, items: [{ name: 'x', layer: 'l', note: '', file: 'docker-compose.yml', line: 34 }] },
  ],
  services: [{ id: 's1', name: 'auth', health: 'warning', layers: [{ name: 'API', health: 'ok', types: 3 }] }],
};

function mockFetch(res: () => Response | Promise<Response>) {
  const fn = vi.fn(async (..._args: unknown[]) => res());
  vi.stubGlobal('fetch', fn);
  return fn;
}
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

beforeEach(() => localStorage.clear());
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('экран «Обзор»', () => {
  it('без пути — пустое состояние со ссылкой на /map, запрос не идёт', async () => {
    const f = mockFetch(() => json(base));
    renderApp('/overview');
    expect(await screen.findByText('Проект не выбран.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Открыть карту и выбрать проект/ })).toHaveAttribute('href', '/map');
    expect(f).not.toHaveBeenCalled();
  });

  it('путь берётся из localStorage (af_path)', async () => {
    localStorage.setItem('af_path', '/proj');
    const f = mockFetch(() => json(base));
    renderApp('/overview');
    await screen.findByText('Критичная находка');
    expect(String(f.mock.calls[0][0])).toContain('path=%2Fproj');
  });

  it('загрузка → скелет с role=status', async () => {
    mockFetch(() => new Promise<Response>(() => {}));
    renderApp('/overview?path=/p');
    expect(await screen.findByRole('status', { name: /Разбираю/ })).toBeInTheDocument();
  });

  it('ошибка → короткое сообщение, без стектрейса', async () => {
    mockFetch(() => json({ error: 'Папка не найдена' }, 404));
    renderApp('/overview?path=/p');
    expect(await screen.findByRole('alert')).toHaveTextContent('Папка не найдена');
  });

  it('пустой проект → объяснение причины', async () => {
    mockFetch(() => json({ ...base, nodes: [], edges: [], problems: [] }));
    renderApp('/overview?path=/p');
    expect(await screen.findByText(/не найдено ни одного сервиса/)).toBeInTheDocument();
  });

  it('нормальные данные: находки по серьёзности, доказательство, ссылка на /problems/:id', async () => {
    mockFetch(() => json(base));
    renderApp('/overview?path=/p');
    await screen.findByText('Критичная находка');
    const links = (await screen.findAllByRole('link')).filter((l) => l.getAttribute('href')?.startsWith('/problems/'));
    expect(links.map((l) => l.textContent)).toEqual([expect.stringContaining('Критичная находка'), expect.stringContaining('Инфо-находка')]);
    expect(links[0]).toHaveAttribute('href', '/problems/p%2Fcrit?path=%2Fp');
    expect(links[0]).toHaveTextContent('docker-compose.yml:34');
    expect(links[0]).toHaveTextContent('Общая база данных (shared-db)');
    expect(links[0]).toHaveTextContent('уверенность 90%');
    expect(links[1]).toHaveTextContent('уверенность не указана');
    expect(screen.getByText(/точно \(по маркеру\) 10% · эвристика 50% · неизвестно 40%/)).toBeInTheDocument();
    expect(screen.getByText(/Менять осторожно/)).toBeInTheDocument();
    expect(screen.getByText(/появится после экрана «Влияние»/)).toBeInTheDocument();
  });

  it('больше 8 находок: лимит и «Показать все»', async () => {
    const many = Array.from({ length: 11 }, (_, i) => ({ id: `m${i}`, severity: 'warning', kind: 'k', title: `Находка ${i}`, location: 'x' }));
    mockFetch(() => json({ ...base, problems: many }));
    renderApp('/overview?path=/p');
    const count = () => screen.getAllByRole('link').filter((l) => l.getAttribute('href')?.startsWith('/problems/')).length;
    await screen.findByText('Находка 0');
    expect(count()).toBe(8);
    await userEvent.click(screen.getByRole('button', { name: 'Показать все (11)' }));
    expect(count()).toBe(11);
    await userEvent.click(screen.getByRole('button', { name: 'Свернуть' }));
    expect(count()).toBe(8);
  });

  it('TrustCard: несколько сервисов, сервисов в покрытии из общего числа', async () => {
    const cov = (u: number) => ({ marker: 10 - u, heuristic: 0, unknown: u, total: 10 });
    mockFetch(() => json({
      ...base,
      nodes: [...base.nodes, { id: 's2', name: 'billing', kind: 'service' }, { id: 's3', name: 'orphan', kind: 'service' }],
      services: [
        { id: 's1', name: 'auth', health: 'ok', layers: [], coverage: cov(2) },
        { id: 's2', name: 'billing', health: 'ok', layers: [], coverage: cov(7) },
      ],
    }));
    renderApp('/overview?path=/p');
    const rows = within(await screen.findByRole('list', { name: 'Покрытие по сервисам' })).getAllByRole('listitem');
    expect(rows[0]).toHaveTextContent('billing');
    expect(rows[1]).toHaveTextContent('auth');
    expect(screen.getByText(/по разобранным сервисам: 2 из 3/)).toBeInTheDocument();
  });

  it('localStorage бросает исключение — экран показывает «Проект не выбран»', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied'); });
    mockFetch(() => json(base));
    renderApp('/overview');
    expect(await screen.findByText('Проект не выбран.')).toBeInTheDocument();
  });

  it('имена из чужих репозиториев выводятся текстом, не разметкой', async () => {
    const evil = '<script>window.__pwned=1</script>';
    mockFetch(() => json({
      ...base,
      nodes: [{ id: 'x', name: evil, kind: 'service' }],
      problems: [{ id: 'e', severity: 'critical', kind: 'k', title: evil, location: 'x' }],
    }));
    const { container } = renderApp('/overview?path=/p');
    expect((await screen.findAllByText(evil)).length).toBeGreaterThan(0);
    expect(container.querySelector('script')).toBeNull();
    expect((window as unknown as Record<string, unknown>).__pwned).toBeUndefined();
  });

  it('клик по мини-карте ведёт на /map', async () => {
    mockFetch(() => json(base));
    renderApp('/overview?path=/p');
    await userEvent.click(await screen.findByRole('link', { name: /Открыть карту/ }));
    expect(await screen.findByText('legacy-map-stub')).toBeInTheDocument();
  });

  it('частичный разбор: баннер «чисто ≠ проблем нет» с деградациями', async () => {
    mockFetch(() => json({ ...base, problems: [], degradations: ['DI не прочитан в auth'] }));
    renderApp('/overview?path=/p');
    const banner = await screen.findByText(/отсутствие находок не значит/);
    expect(banner.closest('[role=status]')).toHaveTextContent('DI не прочитан в auth');
    expect(screen.getByText(/Находок нет\./)).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Что не так' })).getByText(/Это не значит, что проблем нет/)).toBeInTheDocument();
  });
});
