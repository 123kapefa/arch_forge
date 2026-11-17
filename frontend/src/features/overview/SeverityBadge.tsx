import { Badge, type BadgeTone } from '../../shared/ui';
import type { Severity } from './model';

const VIEW: Record<Severity, { tone: BadgeTone; icon: string; label: string }> = {
  critical: { tone: 'error', icon: '●', label: 'Критично' },
  warning: { tone: 'warning', icon: '▲', label: 'Предупреждение' },
  coverage: { tone: 'unknown', icon: '○', label: 'Покрытие' },
  info: { tone: 'unknown', icon: 'ℹ', label: 'Инфо' },
  unknown: { tone: 'unknown', icon: '?', label: 'Не классифицировано' },
};

export function SeverityBadge({ severity }: { severity: Severity }) {
  const v = VIEW[severity];
  return <Badge tone={v.tone} icon={v.icon}>{v.label}</Badge>;
}
