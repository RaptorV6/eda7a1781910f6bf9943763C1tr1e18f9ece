#!/usr/bin/env bash
# SessionStart hook — orient Claude on resume / new session.
#
# Reminder-only: prints a short pointer to the project memory layer.
# Does NOT modify files.
#
# Disable: set CLAUDE_HOOKS_DISABLE_SESSIONSTART=1 or remove the entry
# from .claude/settings.json.

set -euo pipefail

if [[ "${CLAUDE_HOOKS_DISABLE_SESSIONSTART:-}" == "1" ]]; then
  exit 0
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

if [[ ! -f "$repo_root/CLAUDE.md" ]]; then
  # No context architecture yet — stay quiet.
  exit 0
fi

current_task="$repo_root/docs/ai-context/current-task.md"
handoff="$repo_root/docs/ai-context/session-handoff.md"

cat <<EOF
[hook: session-start]
Project memory layer is in place. Before answering anything project-specific:
  - CLAUDE.md (always-loaded router)
  - docs/ai-context/current-task.md $( [[ -f "$current_task" ]] && echo "(present)" || echo "(MISSING)" )
  - docs/ai-context/session-handoff.md $( [[ -f "$handoff" ]] && echo "(present)" || echo "(MISSING)" )
  - docs/ai-context/decisions.md, failed-approaches.md, project-facts.md, environment.md, commands.md
Memory-first rule: search docs/ai-context/ before asking the user to repeat anything.
Skills: context-maintenance, lesson-capture, memory-lookup.
EOF
