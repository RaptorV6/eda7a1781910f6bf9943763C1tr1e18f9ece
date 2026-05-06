#!/usr/bin/env bash
# UserPromptSubmit hook — correction / memory-lookup detector.
#
# Reminder-only: prints a short hint to Claude when the user prompt looks like
# a correction or "we already discussed this" cue. Does NOT modify files.
#
# Disable: set CLAUDE_HOOKS_DISABLE_CORRECTION=1 in your environment, or
# remove the entry from .claude/settings.json.

set -euo pipefail

if [[ "${CLAUDE_HOOKS_DISABLE_CORRECTION:-}" == "1" ]]; then
  exit 0
fi

# The hook receives the user's prompt as JSON on stdin (Claude Code convention).
# We grep its lowercased content for correction/memory-lookup patterns.
input="$(cat || true)"
prompt="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]')"

# Strip JSON wrapping if present — keep this loose; we only need to find phrases.
matches=()

check() {
  local needle="$1"
  local label="$2"
  if printf '%s' "$prompt" | grep -qF -- "$needle"; then
    matches+=("$label")
  fi
}

# English cues
check "we already tried"          "we already tried"
check "we already discussed"      "we already discussed"
check "i already told you"        "I already told you"
check "this was already decided"  "this was already decided"
check "do not use"                "do not use"
check "don't use"                 "don't use"
check "remember that"             "remember that"
check "in this environment"       "in this environment"
check "this project uses"         "this project uses"
check "that assumption is wrong"  "that assumption is wrong"
check "instead use"               "instead use"
check "instead, use"              "instead, use"
check "never do this again"       "never do this again"
check "previous chat"             "previous chat"
check "you should know"           "you should know"
check "it must be done through"   "it must be done through"

# Czech cues (Czech-language project — see project-facts.md)
check "už jsme zkoušeli"          "už jsme zkoušeli"
check "to už jsme řešili"         "to už jsme řešili"
check "to už jsem ti říkal"       "to už jsem ti říkal"
check "to už jsem říkal"          "to už jsem říkal"
check "už jsem ti říkala"         "už jsem ti říkala"
check "to už bylo rozhodnuto"     "to už bylo rozhodnuto"
check "nepoužívej"                "nepoužívej"
check "pamatuj"                   "pamatuj"
check "místo toho"                "místo toho"

if [[ ${#matches[@]} -eq 0 ]]; then
  exit 0
fi

cat <<EOF
[hook: correction-detector]
The user's prompt contains correction / memory-lookup cues: ${matches[*]}.
Before responding:
  1. Use the memory-lookup skill to search docs/ai-context/ for the relevant entry.
  2. If the lesson is missing or stale, use the lesson-capture skill to persist it.
The user should not have to repeat themselves — treat reminders as a memory-system bug.
EOF
