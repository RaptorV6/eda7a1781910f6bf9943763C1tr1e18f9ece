# Tool output policy

Claude must prevent context bloat. Tool outputs are the #1 source of waste.

## General rule

Keep only **high-signal excerpts** in active context. Summarise; do not paste.

## StoreFront / HTTP responses (this project's biggest offender)

StoreFront returns full HTML pages and large XML/JSON bodies. The proxy already truncates to 1200 chars (`CitrixExplicitAuth.Preview`). When inspecting probe/login responses:

Keep:
- Endpoint URL, HTTP method.
- Status code + reason phrase.
- Cookie names (not values, except for `CsrfToken` presence flag).
- The `<Result>` / `<Message>` / first `<Label type="error">` from XML.
- The first 200–400 chars of the body preview if it shows a redirect target / form structure.

Do not keep:
- Full HTML body.
- Full cookie values (security + bloat).
- Repeated identical bodies across hops — note "same as bootstrap" instead.

## Long logs

Keep:
- The command that produced them.
- The error block (10–30 lines around the failure).
- A one-line interpretation.

Do not keep:
- Full log files.
- Repeated `INFO`-level entries.
- Unrelated warnings.

For `dotnet build` output, keep the warning/error summary, not the per-file restore lines.

## Test output

Not applicable yet (no test project). When added:

Keep:
- Command, failing test name, assertion message, stack trace excerpt.

Do not keep:
- Passing test list, unrelated test logs.

## File reads

Prefer:
- The relevant function / class / config section.
- Use `offset`/`limit` on `Read` for large files.

Avoid:
- Reading `Program.cs` in full repeatedly. The 1100-line `Program.cs` should be read in targeted ranges once you know what you're looking for.
- Re-reading `Citrix/**/web.config` files — they are reference material, summarise once into `project-map.md` if they prove relevant.

## API output

Keep:
- Method, endpoint, status code, the 1–3 response fields that matter for the next step, error body excerpt.

Do not keep:
- Full response JSON if only one field is needed.

## Re-fetchable data

Store **how to re-fetch**, not the full output. Example:

> See `appsettings.json::CitrixDiagnostics:BaseUrl` for the StoreFront URL.

> Re-fetch with: `curl -sX POST .../api/citrix-diagnostics/server-probe -d '{...}'`

## Generated artefacts (`bin/`, `obj/`, `publish/`)

Never read these. They are output, not source. If a question requires their content, run the build and read the relevant artefact path; do not commit it to context.

## When subagents return

Subagents (`context-auditor`, `codebase-scout`, generic) **must return summaries**, not raw dumps. If a subagent returns a full file or grep dump, ask it to re-summarise rather than paste the dump.

## Pre-compact

Before `/compact`, take one last pass and drop anything that:

- Has been persisted to a `docs/ai-context/*.md` file already.
- Is re-fetchable from the codebase.
- Is a tool output that no longer affects the next step.
