// Тесты границ на фикстурах: во временном src/ создаём файлы и гоняем скрипт отдельным процессом.
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';

const SCRIPT = resolve(dirname(fileURLToPath(import.meta.url)), 'check-boundaries.mjs');
const dirs = [];

function run(files) {
  const src = mkdtempSync(join(tmpdir(), 'boundaries-'));
  dirs.push(src);
  for (const [rel, text] of Object.entries(files)) {
    const p = join(src, rel);
    mkdirSync(dirname(p), { recursive: true });
    writeFileSync(p, text);
  }
  const r = spawnSync(process.execPath, [SCRIPT, src], { encoding: 'utf8' });
  return { code: r.status, out: r.stdout + r.stderr };
}

afterEach(() => { while (dirs.length) rmSync(dirs.pop(), { recursive: true, force: true }); });

const legacy = { 'legacy/SystemView.ts': 'export const v = 1;\n' };

describe('check-boundaries', () => {
  it('чистый набор → 0', () => {
    const r = run({
      ...legacy,
      'shared/ui/A.ts': 'export const a = 1;\n',
      'features/a/A.ts': "import { a } from '../../shared/ui/A';\nexport const x = a;\n",
      'features/map/legacy-adapter.tsx': "import { v } from '../../legacy/SystemView';\nexport const m = v;\n",
      'app/routes.ts': "import('../features/a/A');\n",
    });
    expect(r.code).toBe(0);
  });

  it('legacy вне адаптера → 1', () => {
    const r = run({ ...legacy, 'features/b/B.ts': "import { v } from '../../legacy/SystemView';\nexport const x = v;\n" });
    expect(r.code).toBe(1);
    expect(r.out).toMatch(/legacy-adapter/);
  });

  it('динамический import legacy вне адаптера → 1', () => {
    const r = run({ ...legacy, 'app/x.ts': "export const l = () => import('../legacy/SystemView');\n" });
    expect(r.code).toBe(1);
  });

  it('feature → feature → 1', () => {
    const r = run({
      'features/a/A.ts': 'export const a = 1;\n',
      'features/b/B.ts': "import { a } from '../a/A';\nexport const x = a;\n",
    });
    expect(r.code).toBe(1);
    expect(r.out).toMatch(/другой экран/);
  });

  it('shared → features и legacy → новый код → 1', () => {
    expect(run({ 'features/a/A.ts': 'export const a = 1;\n', 'shared/lib/s.ts': "import '../../features/a/A';\n" }).code).toBe(1);
    expect(run({ 'shared/lib/s.ts': 'export const s = 1;\n', 'legacy/L.ts': "import '../shared/lib/s';\n" }).code).toBe(1);
  });
});
