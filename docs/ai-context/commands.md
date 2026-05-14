# Commands

Verified commands for this project. Do not add commands here unless they have been actually run or confirmed by the user.

---

## Build

```bash
dotnet build CitrixComponent.csproj
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

There is **no test project**. Do not run `dotnet test`. To add tests, propose creating a sibling `CitrixComponent.Tests/` directory with xUnit (or whatever the user prefers).

---

## Lint / format

```bash
dotnet format CitrixComponent.csproj
```

Result/notes:
- **Not yet verified.** Project has no `.editorconfig` checked in. Confirm with the user before running.

---

## Publish

```bash
rm -rf ./publish
dotnet publish CitrixComponent.csproj -c Release -o ./publish
```

Result/notes:
- Verified. Output goes to `publish/` at repo root.
- **Kompletní deployment workflow (Codespaces → server):**
  1. Codespaces: `rm -rf ./publish && dotnet publish -c Release -o ./publish`
  2. Codespaces: `git add` + `git commit` + `git push` (publish složka je součástí commitu)
  3. Lokální stroj: `git pull`
  4. Lokální stroj: zkopírovat `publish/` na deployment server

---

## Restore

```bash
dotnet restore CitrixComponent.csproj
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

## Call CitrixAuth probe (PowerShell — deployment server)

```powershell
Invoke-RestMethod -Uri "https://localhost/api/citrix-diagnostics/citrixauth-probe" -Method POST -SkipCertificateCheck
```

Result/notes:
- `-SkipCertificateCheck` required — deployment server uses self-signed cert, PowerShell rejects it otherwise (error: "Nadřízené připojení bylo uzavřeno").
- Returns `{status, headers, body}` — `body` is the CitrixAuth/Login response from StoreFront (expected XML).
- Alternative if HTTP port known: `Invoke-RestMethod -Uri "http://localhost:<port>/api/citrix-diagnostics/citrixauth-probe" -Method POST`

---

## Caveats

- Anything other than `dotnet build` will trigger a Claude Code permission prompt unless added to `.claude/settings.local.json`.
- Build artefacts under `bin/`, `obj/`, `publish/` are excluded by `.gitignore` (added 2026-05-06). If you see them tracked, the gitignore was bypassed somewhere — investigate.
