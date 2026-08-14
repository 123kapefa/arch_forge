# CLAUDE.md

Карта проекта, hard-rules и workflow — в `AGENTS.md` (единый источник; его же читает Codex).
Claude Code сам `AGENTS.md` не подхватывает, поэтому импортируем его сюда:

@AGENTS.md

## Как это работает

- **Скилы** (`.ai/skills/*` → `~/.claude/skills`) — Claude вызывает сам, сопоставляя задачу с их `description`. Можно и явно: `/имя-скила`.
- **Субагенты** (`.ai/agents/*` → `.claude/agents`) — Claude сам делегирует по `description`. Явно: `@имя-субагента`.
- **Правила** (`.ai/rules/*` → `.claude/rules`) — авто-загружаются в каждую сессию.
- **Хуки** (профиль `safe`) — в `.claude/settings.json`, срабатывают на события инструментов.

Материализация в эти форматы — `ai-starter setup`. `.claude/` руками не редактируется.
