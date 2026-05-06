---
name: memory-lookup
description: Use BEFORE asking the user to repeat something, before making a project-specific assumption, when the user says "we already discussed", "I told you", "this was decided", or whenever you are about to retry an approach you might have tried before. Searches docs/ai-context/ first so the user does not have to act as your memory.
---

# Memory Lookup Skill

## When to invoke

**Before** doing any of these:

- Asking the user to repeat / re-explain something.
- Making a guess about a project-specific behaviour.
- Retrying an approach that might have been tried (and might have failed) before.
- Answering a question that starts with "I think this project does …".

**On detecting** any of these phrases from the user:

- "we already discussed this"
- "I already told you"
- "this was already decided"
- "we already tried this"
- "this is in the previous chat"
- "you should know this"
- "it worked differently last time"

Treat the user phrase as a memory-system bug signal: search **and** verify the entry exists. If not, capture it (delegate to `lesson-capture`).

## Goal

Find the answer in durable project memory before paying the user the tax of re-explanation.

## Files to search (in priority order)

1. `CLAUDE.md` — always-on rules
2. `docs/ai-context/current-task.md` — in-flight task
3. `docs/ai-context/session-handoff.md` — last session's exit
4. `docs/ai-context/decisions.md` — durable choices
5. `docs/ai-context/failed-approaches.md` — do-not-repeat list
6. `docs/ai-context/project-facts.md` — stable facts
7. `docs/ai-context/environment.md` — runtime / network quirks
8. `docs/ai-context/commands.md` — verified commands
9. `docs/ai-context/conventions.md` — style / preferences
10. `docs/ai-context/project-map.md` — where things live

## Workflow

1. **State** what you're looking for, in one sentence (internal — keeps the query precise).
2. **Search.** Use `Grep` / `Read` across the files above. Prefer `Grep` with a 3–5 word pattern over reading whole files.
3. **Decide:**
   - **Found** → say one line:
     > Found in project memory: `<file>` — `<short summary>`. Continuing with that.
     Then continue the task. Do **not** ask the user to confirm something memory already answers.
   - **Partially found** → state what you found, ask the *minimal* missing piece:
     > Found `<X>` in `<file>`; need to confirm `<Y>` only.
   - **Not found** → say one line:
     > Did not find this in project memory. I will record it once clarified so it survives future chats.
     Then ask the smallest possible clarifying question. After the user answers, **invoke `lesson-capture`** to persist it.

4. **If user said "we already tried" / "we already discussed":** even if you don't find the entry, treat its absence as a bug. After clarification, **always** invoke `lesson-capture` so the same reminder isn't needed again.

## Rules

- Search before asking. **Always.**
- Prefer **`Grep`** over `Read` for the search step — cheaper and you don't pollute context with whole files.
- Do **not** read every memory file every time. Pick the 1–3 most likely targets from the priority list.
- Do **not** invent an answer if memory is silent. Ask, then capture.
- Do **not** quote secrets back at the user even if memory had them — but memory should not contain secrets in the first place.

## Allowed responses (templates)

When found:

> Found in project memory: `docs/ai-context/failed-approaches.md` (`2026-05-06 — GET-only on ExplicitAuth/Login`). This deployment's IIS returns 404 on GET; we POST first. Continuing.

When partially found:

> `docs/ai-context/project-facts.md` says CsrfToken rotates per hop. I cannot find a note about CtxsAuthId rotation specifically — does it rotate too?

When not found:

> Not in project memory yet. Quick clarification: <minimal question>? I'll record the answer in `<target file>` so it survives the next compaction.

## Anti-patterns (do not do these)

- "I don't have access to previous chats." — Almost never the right response. Search memory first.
- "Could you remind me?" — Search memory first. Only ask if memory is genuinely silent.
- Asking the user the same question twice in one session — that's a sign you skipped the search.
- Searching memory but not actually using what you found.
