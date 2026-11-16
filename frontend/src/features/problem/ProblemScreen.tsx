import { useParams } from 'react-router';
import { StubScreen } from '../../shared/ui';

export function ProblemScreen() {
  const { id } = useParams();
  return <StubScreen title="Проблема" description="Одна находка: что не так, где в коде, чем грозит." params={{ id }} />;
}
