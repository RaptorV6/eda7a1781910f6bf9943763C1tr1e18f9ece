#!/usr/bin/env bash
# PostToolUse hook — large-output reminder.
#
# Reminder-only: when a Bash tool produces output exceeding a threshold,
# print a one-line nudge to summarise rather than retain the full dump.
# Does NOT modify the output, does NOT truncate it.
#
# Disable: set CLAUDE_HOOKS_DISABLE_LARGEOUTPUT=1 or remove the entry
# from .claude/settings.json.
#
# This hook reads the tool result on stdin (Claude Code passes JSON containing
# the tool output). We measure character count and emit a hint if large.

set -euo pipefail

if [[ "${CLAUDE_HOOKS_DISABLE_LARGEOUTPUT:-}" == "1" ]]; then
  exit 0
fi

THRESHOLD="${CLAUDE_HOOKS_LARGEOUTPUT_THRESHOLD:-4000}"

input="$(cat || true)"
size=${#input}

if (( size < THRESHOLD )); then
  exit 0
fi

cat <<EOF
[hook: large-output-reminder]
Tool output is ${size} chars (threshold ${THRESHOLD}). Per docs/ai-context/tool-output-policy.md:
  - keep command + status + relevant excerpt only
  - do not preserve full logs in active context
  - record how to re-fetch instead of the full body
EOF
