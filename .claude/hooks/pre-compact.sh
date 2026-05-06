#!/usr/bin/env bash
# PreCompact hook — checkpoint reminder before /compact.
#
# Reminder-only: prints the pre-compact checklist. Does NOT modify files.
#
# Disable: set CLAUDE_HOOKS_DISABLE_PRECOMPACT=1 or remove the entry
# from .claude/settings.json.

set -euo pipefail

if [[ "${CLAUDE_HOOKS_DISABLE_PRECOMPACT:-}" == "1" ]]; then
  exit 0
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

cat <<EOF
[hook: pre-compact]
About to compact. Run the context-maintenance skill FIRST so important state
survives the compaction. Checklist:
  [ ] docs/ai-context/current-task.md updated (objective, status, next step)
  [ ] docs/ai-context/session-handoff.md updated
  [ ] decisions.md appended for any new durable decisions
  [ ] failed-approaches.md appended for any approaches tried-and-rejected
  [ ] project-facts.md / environment.md appended for any user corrections
  [ ] commands.md appended for any newly verified commands

If any of the above is skipped, the next session will re-discover the same
lessons and the user will have to repeat themselves.

Repo root: $repo_root
EOF
