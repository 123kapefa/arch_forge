// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderApp } from '../test/renderApp';

describe('оболочка', () => {
  it('показывает название проекта и навигацию по экранам', async () => {
    renderApp('/overview');
    expect(await screen.findByText('arch_forge')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Экраны' });
    for (const name of ['Старт', 'Обзор', 'Карта', 'Срезы', 'Дифф', 'Отчёт', 'Состояния']) {
      expect(within(nav).getByRole('link', { name })).toBeInTheDocument();
    }
  });

  it('навигация переключает экраны', async () => {
    renderApp('/overview');
    await userEvent.click(await screen.findByRole('link', { name: 'Срезы' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Срезы' })).toBeInTheDocument();
  });
});
