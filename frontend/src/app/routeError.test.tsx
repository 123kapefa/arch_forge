// @vitest-environment jsdom
import { vi, describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderApp } from '../test/renderApp';

// Реальные маршруты, но один экран падает при рендере.
vi.mock('../features/slices/SlicesScreen', () => ({
  SlicesScreen: () => { throw new Error('сломано'); },
}));

describe('error boundary маршрута', () => {
  it('падение экрана показывает ошибку, а навигация остаётся', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    renderApp('/slices');
    expect(await screen.findByRole('alert')).toHaveTextContent('Что-то пошло не так');
    expect(screen.getByText('сломано')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Экраны' });
    expect(within(nav).getByRole('link', { name: 'Обзор' })).toBeInTheDocument();
    spy.mockRestore();
  });
});
