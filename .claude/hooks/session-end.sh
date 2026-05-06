#!/usr/bin/env bash
# Stop / SessionEnd hook — handoff reminder.
#
# Reminder-only: prints the end-of-session checklist. Does NOT modify files.
#
# Disable: set CLAUDE_HOOKS_DISABLE_SESSIONEND=1 or remove the entry
# from .claude/settings.json.

set -euo pipefail

if [[ "${CLAUDE_HOOKS_DISABLE_SESSIONEND:-}" == "1" ]]; then
  exit 0
fi

cat <<EOF
[hook: session-end]
Before ending this session, ensure:
  [ ] docs/ai-context/current-task.md reflects current objective + next step
  [ ] docs/ai-context/session-handoff.md gives the next session a cold-start point
  [ ] Any new decisions/failures/lessons are persisted (lesson-capture skill)
  [ ] No secrets / passwords / real session cookies were written into memory files

Run the context-maintenance skill if any of the above is uncertain.
EOF
