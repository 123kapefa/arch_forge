import { useParams } from 'react-router';
import { StubScreen } from '../../shared/ui';

export function ImpactScreen() {
  const { node } = useParams();
  return <StubScreen title="Влияние" description="Что заденет изменение узла: кто от него зависит." params={{ node }} />;
}
