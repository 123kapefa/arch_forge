#!/usr/bin/env bash
# Материализация канона .ai/ в нативные форматы Claude Code и Codex.
# .ai/ — единственный источник правды; .claude/ генерируется, руками не редактируется.
# Идемпотентно: повторный запуск безопасен.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

green() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
dim()   { printf '  \033[2m%s\033[0m\n' "$1"; }
warn()  { printf '  \033[33m!\033[0m %s\n' "$1"; }

printf '\n\033[1mmaterialize .ai/ → Claude Code + Codex\033[0m  (%s)\n' "$ROOT"

# --- Скилы: симлинки в ~/.claude/skills и ~/.codex/skills -------------------
if [ -d "$ROOT/.ai/skills" ]; then
  for target in "$HOME/.claude/skills" "$HOME/.codex/skills"; do
    mkdir -p "$target"
    n=0
    for d in "$ROOT"/.ai/skills/*/; do
      [ -d "$d" ] || continue
      name="$(basename "$d")"
      ln -sfn "$d" "$target/$name"
      n=$((n + 1))
    done
    green "скилов слинковано: $n → $target"
  done
fi

# --- Субагенты: копия в .claude/agents --------------------------------------
if [ -d "$ROOT/.ai/agents" ]; then
  mkdir -p "$ROOT/.claude/agents"
  n=0
  for f in "$ROOT"/.ai/agents/*.md; do
    [ -e "$f" ] || continue
    cp "$f" "$ROOT/.claude/agents/"
    n=$((n + 1))
  done
  [ "$n" -gt 0 ] && green "субагентов: $n → .claude/agents"
fi

# --- Правила: копия в .claude/rules (авто-загрузка в Claude) -----------------
if [ -d "$ROOT/.ai/rules" ]; then
  mkdir -p "$ROOT/.claude/rules"
  n=0
  for f in "$ROOT"/.ai/rules/*.md; do
    [ -e "$f" ] || continue
    cp "$f" "$ROOT/.claude/rules/"
    n=$((n + 1))
  done
  [ "$n" -gt 0 ] && green "правил: $n → .claude/rules"
fi

# --- Хуки: влить блок hooks в .claude/settings.json (ФИКС #1) ----------------
if [ -f "$ROOT/.ai/hooks/settings.hooks.json" ]; then
  if command -v python3 >/dev/null 2>&1; then
    python3 - "$ROOT" <<'PY'
import json, os, sys
root = sys.argv[1]
src = os.path.join(root, ".ai", "hooks", "settings.hooks.json")
dst = os.path.join(root, ".claude", "settings.json")
with open(src, encoding="utf-8") as f:
    block = json.load(f)
hooks = block.get("hooks", {})
data = {}
if os.path.exists(dst):
    try:
        with open(dst, encoding="utf-8") as f:
            data = json.load(f)
    except ValueError:
        data = {}
data["hooks"] = hooks
os.makedirs(os.path.dirname(dst), exist_ok=True)
with open(dst, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
print("  \033[32m✓\033[0m хуки вписаны в .claude/settings.json")
PY
  else
    warn "python3 не найден — хуки не подключены к Claude"
  fi
fi

# --- MCP: по умолчанию не включаем ------------------------------------------
if [ -d "$ROOT/.ai/mcp" ]; then
  dim "MCP-серверы по умолчанию выключены (см. .ai/mcp/, включать отдельно)"
fi

printf '\n\033[1mГотово.\033[0m Проверка: \033[36mai-starter doctor\033[0m\n\n'
