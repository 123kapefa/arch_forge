import type { GraphDto, NodeDto } from './api';
import { layerColor } from './theme';

/** Панель инспектора узла: роль/честность, дрейф, зависимости, якорь на исходник (P1-E). */
export function Inspector({ node, graph, onClose, onFocus }: {
  node: NodeDto;
  graph: GraphDto;
  onClose: () => void;
  onFocus: (id: string) => void;
}) {
  const outgoing = graph.edges.filter((e) => e.source === node.id);
  const incoming = graph.edges.filter((e) => e.target === node.id);
  const nameOf = (id: string) => graph.nodes.find((n) => n.id === id)?.name ?? id;
  const color = layerColor(node.layer);
  // Путь в IR абсолютный → ссылка открывает файл в VS Code (один слэш: macOS `/…`, Windows `/C:/…`).
  const filePath = node.file.replace(/\\/g, '/');
  const codeUrl = `vscode://file${filePath.startsWith('/') ? '' : '/'}${filePath}`;

  return (
    <aside className="insp">
      <div className="insp__head" style={{ borderTopColor: color }}>
        <div className="insp__title">
          <div className="insp__meta" style={{ color }}>{node.layer} · {node.kind}</div>
          <div className="insp__name" title={node.name}>{node.name}</div>
        </div>
        <button className="insp__close" onClick={onClose} title="закрыть">✕</button>
      </div>

      <div className="insp__role">
        <span>{node.role}</span>
        <span className={`insp__via via-${node.via}`}>{node.via}</span>
      </div>

      {node.drift.length > 0 && (
        <div className="insp__block insp__drift">
          <div className="insp__k">⚠ Дрейф от свода</div>
          <ul>{node.drift.map((d, i) => <li key={i}>{d}</li>)}</ul>
        </div>
      )}

      <div className="insp__block">
        <div className="insp__k">Зависимости · {outgoing.length} исходящих / {incoming.length} входящих</div>
        <ul className="insp__deps">
          {outgoing.map((e) => (
            <li key={`o${e.id}`}><span className="insp__ek">{e.kind}</span> → {nameOf(e.target)}</li>
          ))}
          {incoming.map((e) => (
            <li key={`i${e.id}`} className="insp__inc">{nameOf(e.source)} <span className="insp__ek">{e.kind}</span> →</li>
          ))}
          {outgoing.length === 0 && incoming.length === 0 && (
            <li className="insp__none">нет связей в текущем виде</li>
          )}
        </ul>
      </div>

      <div className="insp__block">
        <div className="insp__k">Исходник</div>
        <div className="insp__file" title={node.file}>{node.file}</div>
      </div>

      <div className="insp__actions">
        <a className="insp__open" href={codeUrl}>Открыть в коде ↗</a>
        <button onClick={() => onFocus(node.id)}>Фокус на связях</button>
      </div>
    </aside>
  );
}
