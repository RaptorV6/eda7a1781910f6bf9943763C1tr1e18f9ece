# Commands

Verified commands for this project. Do not add commands here unless they have been actually run or confirmed by the user.

---

## Build

```bash
dotnet build PortalComponent.csproj
```

Result/notes:
- Verified. Pre-allowed in `.claude/settings.local.json` (`Bash(dotnet build:*)`).
- Targets `net10.0`. Will produce artefacts under `bin/` and `obj/` — these dirty the working tree.

---

## Run (local)

```bash
dotnet run
```

Result/notes:
- **Not yet verified by automation** in this environment. Will start Kestrel on the port configured by `Properties/launchSettings.json` (default ASP.NET Core dev ports).
- HTTPS redirection is on outside Development; in Development, Kestrel typically listens on both http/https.
- Requires network reachability to `citrixvpx01.fis.acr` for the diagnostic flow to succeed end-to-end.

---

## Test

There is **no test project**. Do not run `dotnet test`. To add tests, propose creating a sibling `PortalComponent.Tests/` directory with xUnit (or whatever the user prefers).

---

## Lint / format

```bash
dotnet format PortalComponent.csproj
```

Result/notes:
- **Not yet verified.** Project has no `.editorconfig` checked in. Confirm with the user before running.

---

## Publish

```bash
rm -rf ./publish
dotnet publish -c Release -o ./publish
```

Result/notes:
- Verified. Output goes to `publish/` at repo root. Copy that directory to deployment server.

---

## Restore

```bash
dotnet restore PortalComponent.csproj
```

Result/notes:
- Implicit during `dotnet build`. Run explicitly only when troubleshooting NuGet feed issues.

---

## Debugging the Citrix proxy

To exercise the proxy without the diagnostic UI:

```bash
curl -X POST http://localhost:<port>/api/citrix-diagnostics/server-probe \
  -H "Content-Type: application/json" \
  -d '{"requestId":"manual-1","step":"probe","url":"https://citrixvpx01.fis.acr/Citrix/FISWeb/Authentication/GetAuthMethods","method":"POST"}'
```

Result/notes:
- **Pattern, not verified.** Adapt port + body. The endpoint always returns `Results.Ok(...)` even on probe failure — check the `Ok` field.

---

## Caveats

- Anything other than `dotnet build` will trigger a Claude Code permission prompt unless added to `.claude/settings.local.json`.
- Build artefacts under `bin/`, `obj/`, `publish/` are excluded by `.gitignore` (added 2026-05-06). If you see them tracked, the gitignore was bypassed somewhere — investigate.
