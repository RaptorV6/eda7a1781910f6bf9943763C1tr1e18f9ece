# `.claude/hooks/`

Project-local Claude Code hook scripts. **All hooks are reminder-only** — they print short hints to Claude / the user. None of them modify files, run installers, or take destructive actions.

## Files

| Script | Wired to | Purpose | Disable env var |
|---|---|---|---|
| `session-start.sh` | `SessionStart` | Orient Claude: point at memory layer; flag missing files | `CLAUDE_HOOKS_DISABLE_SESSIONSTART=1` |
| `correction-detector.sh` | `UserPromptSubmit` | Detect "we already tried", "I told you", etc. (CZ + EN); nudge memory-lookup / lesson-capture | `CLAUDE_HOOKS_DISABLE_CORRECTION=1` |
| `pre-compact.sh` | `PreCompact` | Print the pre-compact checklist; remind to run `context-maintenance` skill | `CLAUDE_HOOKS_DISABLE_PRECOMPACT=1` |
| `session-end.sh` | `Stop` | Print the end-of-session handoff checklist | `CLAUDE_HOOKS_DISABLE_SESSIONEND=1` |
| `large-output-reminder.sh` | `PostToolUse` (Bash) | Nudge to summarise when Bash output exceeds threshold | `CLAUDE_HOOKS_DISABLE_LARGEOUTPUT=1` (threshold: `CLAUDE_HOOKS_LARGEOUTPUT_THRESHOLD`, default 4000) |

## Wiring

All hooks are registered in `.claude/settings.json` (project-local). The settings file references these scripts by relative path.

## Disabling

Three options, ranked from least to most intrusive:

1. **Per-hook env var** (above). Surgical, reversible.
2. **Remove from `.claude/settings.json`.** Stops the hook from being called at all.
3. **`chmod -x` the script.** Belt-and-braces; the hook will fail silently if invoked.

## Adding a new hook

1. Drop a `*.sh` script in this directory. Make it executable: `chmod +x .claude/hooks/your-hook.sh`.
2. Always start the script with the disable env var check (copy the shape from existing scripts).
3. Always exit `0` even on "no match" — hooks must not block Claude on routine work.
4. Wire it in `.claude/settings.json` under the appropriate event (`SessionStart`, `UserPromptSubmit`, `PreCompact`, `Stop`, `PostToolUse`, etc.).
5. Document it in this README.
6. Never add a hook that modifies project files silently.

## Safety contract

These scripts:

- Read stdin (where applicable) without trusting it.
- Use `set -euo pipefail`.
- Do **not** write to any file in the repo.
- Do **not** install packages.
- Do **not** call out to the network.
- Do **not** read secrets.
- Are short (under 100 lines each) so they can be audited at a glance.
