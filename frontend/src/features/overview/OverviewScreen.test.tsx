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
afterEach(() => vi.unstubAllGlobals());

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
    expect(links[0]).toHaveTextContent('уверенность 90%');
    expect(links[1]).toHaveTextContent('уверенность не указана');
    expect(screen.getByText(/точно \(по маркеру\) 10% · эвристика 50% · неизвестно 40%/)).toBeInTheDocument();
    expect(screen.getByText(/Менять осторожно/)).toBeInTheDocument();
    expect(screen.getByText(/появится после экрана «Влияние»/)).toBeInTheDocument();
  });

  it('клик по мини-карте ведёт на /map', async () => {
    mockFetch(() => json(base));
    renderApp('/overview?path=/p');
    await userEvent.click(await screen.findByRole('link', { name: 'Открыть карту' }));
    expect(await screen.findByText('legacy-map-stub')).toBeInTheDocument();
  });

  it('частичный разбор: баннер «чисто ≠ проблем нет» с деградациями', async () => {
    mockFetch(() => json({ ...base, problems: [], degradations: ['DI не прочитан в auth'] }));
    renderApp('/overview?path=/p');
    const banner = await screen.findByText(/отсутствие находок не значит/);
    expect(banner.closest('[role=status]')).toHaveTextContent('DI не прочитан в auth');
    expect(screen.getByText(/Находки нет|Находок нет/)).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Что не так' })).getByText(/Это не значит, что проблем нет/)).toBeInTheDocument();
  });
});
