import { QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router';
import { createQueryClient } from '../app/queryClient';
import { routes } from '../app/routes';

/** Рендерит приложение в memory-роутере на заданном адресе. */
export function renderApp(url: string, customRoutes: RouteObject[] = routes) {
  const router = createMemoryRouter(customRoutes, { initialEntries: [url] });
  const queryClient = createQueryClient();
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...utils, router };
}
