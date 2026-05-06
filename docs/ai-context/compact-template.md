# Compact template

When compacting (`/compact`), or before ending a long session, preserve this and discard everything else.

## Current objective
What the session was working toward, in one or two sentences.

## Current status
Where things stand right now.

## Modified files
- `path` — what changed and why

## Inspected files
- `path` — relevance

## Commands run
- `command` — exit code / summary

## Test results
- command — pass/fail/skip; failing test name + assertion

## Important errors
Quoted exact, with the smallest excerpt that proves the failure.

## Decisions
Short bullets. Persist the durable ones into [decisions.md](decisions.md).

## Failed approaches
Short bullets. Persist into [failed-approaches.md](failed-approaches.md) if reusable beyond this task.

## User corrections / lessons learned
Short bullets. Persist into [project-facts.md](project-facts.md) / [environment.md](environment.md) / [conventions.md](conventions.md) as appropriate.

## Environment / project facts discovered
Short bullets. Persist into [environment.md](environment.md) / [project-facts.md](project-facts.md).

## Open questions
Bullet list.

## Next step
The single next best action.

---

## Discard

- Raw long logs
- Repeated conversation
- Obsolete plans
- Full file contents that can be re-read
- Tool outputs that can be re-run
- Irrelevant brainstorming
- Emotional wording
- Speculative dead ends

## Pre-compact checklist (must do BEFORE running `/compact`)

1. Run the `context-maintenance` skill.
2. Verify [current-task.md](current-task.md) reflects current objective + next step.
3. Verify any session-discovered durable lessons are persisted into the right memory file.
4. Verify [session-handoff.md](session-handoff.md) is up to date.
5. Only then run `/compact`.

If any of the above is skipped, future sessions will lose the lesson and the user will have to repeat themselves.
