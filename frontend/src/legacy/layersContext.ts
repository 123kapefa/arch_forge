import { createContext } from 'react';

// Скрытые слои (по имени) — секция «СЛОИ» в панели ВИД прячет их в карточках сервисов.
export const HiddenLayersContext = createContext<ReadonlySet<string>>(new Set());
