import type { GraphDto, EdgeDto } from './api';

/**
 * Ссылка `vscode://file` на исходник. Percent-encode каждого сегмента (кроме диска Windows `C:`),
 * один слэш между схемой и путём (macOS `/…`, Windows `/C:/…`). Иначе `#`/`?`/`%`/пробел в пути
 * ломают URL (`#` → fragment) и «Открыть в коде» молча открывает не тот файл.
 */
export function buildCodeUrl(file: string): string {
  const path = file.replace(/\\/g, '/');
  const encoded = path
    .split('/')
    .map((seg) => (/^[A-Za-z]:$/.test(seg) ? seg : encodeURIComponent(seg)))
    .join('/');
  return `vscode://file${encoded.startsWith('/') ? '' : '/'}${encoded}`;
}

/** Исходящие/входящие рёбра узла в графе. */
export function nodeDeps(graph: GraphDto, id: string): { outgoing: EdgeDto[]; incoming: EdgeDto[] } {
  return {
    outgoing: graph.edges.filter((e) => e.source === id),
    incoming: graph.edges.filter((e) => e.target === id),
  };
}
