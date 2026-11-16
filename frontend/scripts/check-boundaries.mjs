// Границы модулей, которые oxlint не проверяет (adr/0016, docs/tech/frontend-standards.md):
//   1. features/<a> не импортирует features/<b> (экраны независимы; общее — в shared/);
//   2. legacy/ импортируется ТОЛЬКО из features/map/legacy-adapter.tsx;
//   3. слои: shared не знает про features/app; features не знает про app; legacy не знает про новый код.
// Ограничения: проверяются только ОТНОСИТЕЛЬНЫЕ импорты (`./`, `../`); алиасы (`@/…`), `require`,
// `import.meta.glob` не видны — не вводите алиасы, не обновив скрипт.
// Лимит длины файла и запрет dangerouslySetInnerHTML проверяет oxlint (см. .oxlintrc.json).
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

// Каталог src можно передать аргументом (так скрипт гоняют тесты на фикстурах).
const SRC = process.argv[2] ? resolve(process.argv[2]) : resolve(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const ADAPTER = join(SRC, 'features', 'map', 'legacy-adapter.tsx');
// import x from '…', import '…', export … from '…', import('…')
const IMPORT_RE = /(?:\bfrom\s*|\bimport\s*\(?\s*)['"]([^'"]+)['"]/g;

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) yield* walk(p);
    else if (/\.(tsx?|jsx?)$/.test(name)) yield p;
  }
}

/** Верхний каталог под src и (для features) имя экрана. */
function zone(file) {
  const [top, second] = relative(SRC, file).split(sep);
  return { top, feature: top === 'features' ? second : null };
}

const errors = [];
for (const file of walk(SRC)) {
  const from = zone(file);
  const text = readFileSync(file, 'utf8');
  for (const m of text.matchAll(IMPORT_RE)) {
    const spec = m[1];
    if (!spec.startsWith('.')) continue; // пакеты не проверяем
    const target = resolve(dirname(file), spec);
    const to = zone(target);
    const where = `${relative(SRC, file)} → ${spec}`;

    if (to.top === 'legacy' && from.top !== 'legacy' && file !== ADAPTER) {
      errors.push(`${where}: legacy/ можно импортировать только из features/map/legacy-adapter.tsx`);
    }
    if (from.top === 'legacy' && ['app', 'features', 'shared'].includes(to.top)) {
      errors.push(`${where}: legacy/ не должен зависеть от нового кода`);
    }
    if (from.feature && to.top === 'features' && to.feature !== from.feature) {
      errors.push(`${where}: экран не импортирует другой экран (features/${from.feature} → features/${to.feature}); общее вынесите в shared/`);
    }
    if (from.top === 'features' && to.top === 'app') errors.push(`${where}: features не зависят от app/`);
    if (from.top === 'shared' && ['app', 'features'].includes(to.top)) errors.push(`${where}: shared/ не зависит от app/ и features/`);
  }
}

if (errors.length) {
  console.error(`Нарушены границы модулей (${errors.length}):\n` + errors.map((e) => `  • ${e}`).join('\n'));
  process.exit(1);
}
console.log('Границы модулей: ок');
