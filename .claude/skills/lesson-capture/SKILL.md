---
name: lesson-capture
description: Use whenever the user corrects you, rejects an approach, says "we already tried that", says "remember that", or explains environment-specific behaviour. Classifies the correction and writes a durable entry to the right docs/ai-context/ file so it survives compaction and new chats.
---

# Lesson Capture Skill

## When to invoke

Trigger on **any** of these signals from the user:

- "no, that doesn't work"
- "we already tried this"
- "we already discussed this"
- "I already told you"
- "this was already decided"
- "do not use this approach"
- "in this environment / on this server it works differently"
- "this project uses X, not Y"
- "remember that …"
- "never do this again"
- "instead, use …"

Also trigger when:

- A command failed in a way that other sessions could repeat.
- You discovered a project-specific caveat the user did not flag (e.g. a non-obvious StoreFront/IIS quirk).
- An assumption you made was proven wrong by evidence.

If the user phrases the same correction twice, treat it as a critical signal: the existing memory is missing or wrong. Re-check and fix.

## Goal

Convert the correction into a **short, dated, actionable** entry in the correct memory file.

## Classification table

| Type | Target file | Use when |
|---|---|---|
| Project fact | `docs/ai-context/project-facts.md` | Stable project-specific rule, applies indefinitely |
| Environment rule | `docs/ai-context/environment.md` | Runtime / host / network / tool quirk |
| Failed approach | `docs/ai-context/failed-approaches.md` | An attempt that should not be repeated |
| Decision | `docs/ai-context/decisions.md` | A choice between alternatives the project now depends on |
| Command / tool caveat | `docs/ai-context/commands.md` | A command flag, ordering, or limitation |
| Coding convention | `docs/ai-context/conventions.md` | A style/preference rule |
| Always-on rule | `CLAUDE.md` | Only if it must be loaded every session — be conservative |
| Temporary task update | `docs/ai-context/current-task.md` | Only relevant to the in-flight task |

## Entry shape (use exactly this)

```md
## YYYY-MM-DD — Short title

Lesson:
What Claude must remember.

Why it matters:
What mistake this prevents.

Do this:
The correct future behaviour.

Avoid:
The wrong assumption / action.
```

For `decisions.md` use the richer "Status / Decision / Reason / Alternatives / Consequences / Affected files" shape already in the file.

For `failed-approaches.md` use the "Context / Tried / Observed failure / Root cause / Do instead / Do not repeat" shape already in the file.

## Workflow

1. **Identify** the lesson in one sentence.
2. **Classify** it using the table above. If two categories fit, prefer the durable one (failed-approach over current-task; project-fact over decision when it's a fact, not a choice).
3. **Open** the target file. Look for an existing entry on the same topic. If found, **update or supersede** rather than duplicating.
4. **Write** a new entry using the shape above.
5. **Cross-link.** If the lesson is a failed approach that produced a decision, mention the decision in the failed-approaches entry and vice versa.
6. **Confirm**. Print one line: `Recorded: [file] — [short title]`.

## Rules

- **Date in `YYYY-MM-DD`.** Today is whatever the harness reports. Don't invent dates.
- **Short, specific, actionable.** One paragraph max per field.
- **Do not** copy the user's emotional tone. Strip "ugh, again", "for the third time", etc.
- **Do not** record one-off task details in durable files — those go in `current-task.md`.
- **Do not** overwrite or delete existing entries unless superseding. When superseding, mark the old entry `Status: superseded by YYYY-MM-DD — Title`.
- **Do not** record secrets. If a user mentions a password / token / cookie value as part of the correction, omit it.
- **Do** re-read the target file after writing to verify the entry parses as Markdown and the date sort is correct.

## Examples

User: *"Stop trying GET on `ExplicitAuth/Login` — this StoreFront rejects it. POST first, GET only as fallback."*

→ Classify: failed approach + decision. Two entries:

`failed-approaches.md`:
```md
## 2026-05-06 — GET-only on ExplicitAuth/Login

Context: Fetching the auth form definition.
Tried: GET /Citrix/FISWeb/ExplicitAuth/Login.
Observed failure: HTTP 404.
Root cause: This deployment's IIS rejects GET on the explicit-auth endpoint.
Do instead: POST first (empty form-encoded body + CredentialTypes/LabelTypes headers), GET as fallback.
Do not repeat: Removing the POST branch.
```

`decisions.md`:
```md
## 2026-05-06 — POST-first, GET-fallback for ExplicitAuth/Login
Status: accepted
Decision: ...
```

User: *"Remember — on this server PHP is sometimes 5.6 even though we target 7.2."*

→ Classify: environment rule. Entry in `environment.md` under "Runtime / platform" or as a dated "## 2026-05-06 — PHP version drift" subsection.
