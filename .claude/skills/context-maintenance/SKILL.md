---
name: context-maintenance
description: Use before /compact, before /clear, before ending a long session, after major implementation progress, or whenever the user asks for a handoff. Updates docs/ai-context/ memory files so important state survives compaction and new chats.
---

# Context Maintenance Skill

Run this **before** any of:

- `/compact`
- `/clear`
- ending a long session
- a chunky milestone (feature merged, bug fixed, refactor landed)
- the user asking for a "handoff" / "summary for next session"

## Goal

Move important session state from chat history into durable memory files so the next Claude session can resume from cold.

## Files this skill touches

| File | When to update |
|---|---|
| `docs/ai-context/current-task.md` | Always — overwrite to reflect current objective + status + next step |
| `docs/ai-context/session-handoff.md` | Always — overwrite with the next-session restart point |
| `docs/ai-context/decisions.md` | Append if any durable decisions were made this session |
| `docs/ai-context/failed-approaches.md` | Append if any approach was tried and rejected/failed |
| `docs/ai-context/project-facts.md` | Append if a stable project-specific fact was discovered |
| `docs/ai-context/environment.md` | Append if a runtime/host/network quirk was discovered |
| `docs/ai-context/commands.md` | Append if a new command was actually run and verified |
| `docs/ai-context/project-map.md` | Update only if directory layout / entry points changed |
| `docs/ai-context/conventions.md` | Update only if the user introduced a new style/preference |

## Workflow (checklist — create one TodoWrite item per step)

1. **Identify the current objective.** One sentence.
2. **Summarize current status.** One paragraph.
3. **List files modified.** Use `git status` if needed; cross-check with the chat.
4. **List files inspected.** Only the ones whose content matters for the next step.
5. **List commands / tests run.** With short pass/fail summaries.
6. **Extract decisions.** Anything the user accepted or that the code now depends on. → `decisions.md`.
7. **Extract failed approaches.** Anything tried and rejected/erroring. → `failed-approaches.md`.
8. **Extract user corrections / lessons.** Classify and route via the `lesson-capture` skill if non-trivial.
9. **Extract verified commands.** → `commands.md`.
10. **Rewrite `current-task.md`.** Use the template in [docs/ai-context/compact-template.md](../../../docs/ai-context/compact-template.md).
11. **Rewrite `session-handoff.md`.** Concise restart point. The next Claude must be able to pick up from cold by reading `CLAUDE.md` + `current-task.md` + `session-handoff.md`.
12. **Deduplicate.** Skim the files you appended to and remove duplicate entries.
13. **Confirm.** Print a one-line summary of what was updated.

## Rules

- **Concise over comprehensive.** A handoff that takes 30 seconds to read is more useful than one that takes 5 minutes.
- **Persist `Why`, not `What`.** Code answers "what". Memory should answer "why" and "what to avoid".
- **Date entries** with `YYYY-MM-DD`.
- **Do not** copy raw tool output, raw logs, or full files into memory. Reference the file path instead.
- **Do not** write secrets (passwords, tokens, real session cookies) into memory.
- **Do not** delete entries from `decisions.md` or `failed-approaches.md` — supersede instead (mark old entry `Status: superseded` with a pointer to the new one).
- **Do not** create new top-level files without asking the user. Stick to the layer structure.

## Output

End with one line, e.g.:

> Context maintained: updated current-task.md, session-handoff.md, +1 entry in failed-approaches.md (StoreFront LoginAttempt 401 with stale CSRF). Safe to /compact.
