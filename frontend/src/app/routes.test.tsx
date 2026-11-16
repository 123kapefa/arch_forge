// @vitest-environment jsdom
import { vi, describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import { renderApp } from '../test/renderApp';

// Старая карта тяжёлая (react-flow, elk) и в jsdom не нужна: проверяем только, что /map монтирует адаптер.
vi.mock('../features/map/legacy-adapter', () => ({ LegacyMap: () => <div>legacy-map-stub</div> }));

const cases: Array<[string, string]> = [
  ['/', 'Открыть проект'],
  ['/overview', 'Обзор'],
  ['/problems/p-42', 'Проблема'],
  ['/impact/auth-service', 'Влияние'],
  ['/slices', 'Срезы'],
  ['/diff', 'Дифф'],
  ['/report', 'Отчёт'],
  ['/states', 'Состояния'],
];

describe('маршруты', () => {
  it.each(cases)('%s отдаёт экран «%s» внутри оболочки', async (url, title) => {
    renderApp(url);
    expect(await screen.findByRole('heading', { level: 1, name: title })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Экраны' })).toBeInTheDocument();
  });

  it('заглушки помечены «Экран ещё не сделан»', async () => {
    renderApp('/diff');
    expect(await screen.findByText('Экран ещё не сделан')).toBeInTheDocument();
  });

  it('параметры маршрута доходят до экрана', async () => {
    renderApp('/problems/p-42');
    expect(await screen.findByText('p-42')).toBeInTheDocument();
  });

  it('/map монтирует старую карту через адаптер', async () => {
    renderApp('/map');
    expect(await screen.findByText('legacy-map-stub')).toBeInTheDocument();
  });

  it('неизвестный адрес — страница 404 в оболочке', async () => {
    renderApp('/нет-такого');
    expect(await screen.findByRole('heading', { name: 'Страница не найдена' })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Экраны' })).toBeInTheDocument();
  });
});
