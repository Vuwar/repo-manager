# RepoManager

One place that owns every dev server on your Windows machine. Start, stop, restart and read logs from a tray app,
from the `devm` CLI, or from Claude Code through MCP. A Claude Code hook stops sessions from killing or duplicating
servers that someone else is using.

- Every service runs in a Windows Job Object: stopping it kills the whole tree (`npm` → `node`,
  `dotnet run` → `YourApi.exe`), and if RepoManager itself dies, Windows kills everything it started. No orphans,
  no ports or DLLs left locked.
- Port conflicts are checked before start and explained: which PID, which program, which command line.
- Each git worktree of a project can run its own copy on its own ports.
- Services that were running come back when RepoManager starts again.

Design: [docs/superpowers/specs/2026-09-28-repo-manager-design.md](docs/superpowers/specs/2026-09-28-repo-manager-design.md)

## Install

Needs the .NET 10 SDK and Node.js to build.

```powershell
.\install.ps1
```

It builds everything, installs `RepoManager.exe` and `devm.exe` to `%LOCALAPPDATA%\RepoManager\bin` (added to your
PATH), creates a Start Menu shortcut, starts RepoManager in the tray at login, and connects Claude Code (MCP server
`repomanager`, a PreToolUse hook in `~/.claude/settings.json` (backed up first), and a marked block in
`~/.claude/CLAUDE.md`). Rerun it to update. Options: `-NoAutostart`, `-NoClaude`, `-NoBuild`, `-NoStart`.

`.\uninstall.ps1` undoes all of it (`-RemoveData` also deletes the project list, state and logs).

## Add your projects

In the app: **Add project** → pick a folder, or **Scan folder** to add several repos at once. From a terminal:

```powershell
devm scan C:\Users\me\source\repos
devm add C:\Users\me\source\repos\panel-pro
```

A repo that already has `.claude/launch.json` works as is. Add `devservers.json` in the repo root for more:

```json
{
  "services": {
    "api": {
      "env": { "ASPNETCORE_URLS": "http://127.0.0.1:${port:api}" },
      "health": "http://127.0.0.1:${port:api}/health"
    },
    "web": {
      "command": "npm",
      "args": ["run", "dev", "--prefix", "web", "--", "--port", "${port:web}", "--strictPort"],
      "env": { "VITE_API_URL": "http://127.0.0.1:${port:api}" },
      "dependsOn": ["api"],
      "prepare": [{ "if": "!exists(web/node_modules)", "run": "npm ci --prefix web" }],
      "autoPort": true
    }
  },
  "tasks": {
    "publish": { "command": "publish.bat" },
    "test": { "command": "dotnet", "args": ["test"] }
  }
}
```

Three layers are merged field by field: `.claude/launch.json` < `devservers.json` < your machine-only overrides
(edited in the app's Config tab, stored in `%LOCALAPPDATA%\RepoManager\registry.json`). The Config tab shows which
layer every value comes from.

| Field | Meaning |
|---|---|
| `command`, `args`, `cwd`, `env` | What to run (through `cmd.exe`, so `npm`, `.bat` and `.cmd` work), from where (relative to the repo). |
| `port` | Fixed port. Start fails with the owner's details if it is taken (`--kill-owner` kills that owner). |
| `autoPort` | Use `port` when free, otherwise a free port in 20000–29999. |
| `url`, `health` | Link shown in the UI; health URL for readiness (2xx) and health checks every 10 s. |
| `dependsOn` | Started first, and must be ready before this one starts. |
| `prepare` | Steps before start; `if` supports `exists(path)` and `!exists(path)`. |
| `autoRestart` | Restart after a crash (1 s, 2 s, 4 s… backoff; gives up after 5 crashes in 60 s). |
| `readyTimeoutSeconds` | Default 120. |
| `disabled` | Keep the definition but never start it. |

Variables: `${port:<service>}` (the port a service really got), `${root}` (checkout folder), `${env:NAME}`.

### Worktrees

Worktrees of a registered repo show up automatically (`project@<folder>`). Their services always get their own ports
from 20000–29999, kept between restarts. A service that has a fixed port and never mentions `${port:...}` would clash
with the main checkout, so it is refused in worktrees (`--force` overrides). Fix it by reading the port from an
argument or environment variable, e.g. `"args": ["run", "--urls", "http://127.0.0.1:${port:api}"]`.

## devm

Run inside a repo or worktree; short names resolve from the current folder. Full ids work anywhere:
`panel-pro/api`, `panel-pro@feat-x/web`.

```
devm status [--all]
devm start|stop|restart <svc|all> [--kill-owner] [--force]
devm wait <svc>
devm logs <svc> [--tail 100] [--since 5m] [--grep text] [--follow]
devm run <task>
devm url <svc>
devm ports
devm open vscode|rider|explorer|terminal
```

Every command takes `--json`. Exit codes: 0 ok, 1 error, 2 a service failed to start (the last 50 log lines are printed).

## Claude Code

After `install.ps1` (or `devm claude-setup`):

- MCP tools: `list_services`, `service_status`, `start_service`, `stop_service`, `restart_service`, `get_logs`,
  `run_task`, `get_url`.
- The hook denies `taskkill` / `Stop-Process` / `kill-port` aimed at a managed service, running a managed service's
  command directly (`npm run dev`, `dotnet run --project ...`), and `preview_start` by name for a managed service
  (which would launch a second copy from `launch.json`). The message tells Claude which `devm` command to use.
- The hook never blocks when RepoManager is not running, when a match is unclear, or when it errors.

## Develop

```powershell
dotnet build
dotnet test                        # unit + integration tests (spawn real processes, a real daemon, devm and MCP)
cd web; npm install; npm run dev   # UI dev server, proxies /api to 127.0.0.1:4000
```

`RepoManager.exe --headless` runs the daemon without tray or window. `REPOMANAGER_HOME` and `REPOMANAGER_PORT`
move the data folder and API port (useful for a second, test instance).

| Path | What |
|---|---|
| `src/RepoManager.Core` | Config layers, supervisor, Job Objects, ports, logs, git, state, hook policy |
| `src/RepoManager.App` | Tray + WebView2 window, HTTP API, embedded UI (`RepoManager.exe`) |
| `src/RepoManager.Cli` | `devm`: CLI, MCP server, hook, Claude setup |
| `src/RepoManager.Contracts` | API DTOs |
| `web/` | React UI (builds into `src/RepoManager.App/wwwroot`) |
| `tests/` | xUnit tests and `FakeServer`, a scriptable fake dev server |
