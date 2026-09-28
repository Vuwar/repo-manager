# RepoManager — Design

Date: 2026-09-28
Status: Approved design, not yet implemented

## 1. Problem

Several projects (currently 6 under `C:\Users\vuqar.karimli\source\repos`, mostly a .NET API plus a Vite/Next frontend) are worked on at the same time, often by several Claude Code sessions in parallel. Dev servers run in scattered console windows. Claude sessions stop and start servers on their own to test changes, which kills servers other sessions or the user depend on, starts duplicate copies, and leaves orphan processes holding ports and locked DLLs. Fixed ports (5080, 5159, 5173, 5174, …) collide when several projects run together.

## 2. Goals

- One place that owns every dev server process on the machine: start, stop, restart, status, logs.
- The user works through a desktop app (tray icon plus window). Claude works through a CLI and an MCP server. Both see the same processes and the same logs.
- Claude is prevented from killing or duplicating managed servers.
- No orphan processes, ever: stopping a service stops its whole process tree; the manager dying stops everything it started.
- Port conflicts are detected before start and explained (who owns the port).
- Each git worktree of a project can run as its own instance with its own ports.
- The app is easy to install and opens like a normal Windows app.

## 3. Non-goals

- Production deployment, Docker Compose orchestration, CI.
- Linux/macOS support.
- Guessing start commands for repos that have no configuration.
- Terminal UI and a remote/multi-machine mode (possible later, see §12).

## 4. Architecture

```
RepoManager.exe  (one process, single instance via named mutex)
├─ Tray icon + WebView2 window (WinForms)
├─ Daemon core
│   ├─ ConfigService      loads the 3 config layers, merges, watches files
│   ├─ ProcessSupervisor  spawns services in Job Objects, tracks state
│   ├─ PortService        conflict check, owner lookup, auto-assign
│   ├─ LogStore           ring buffer per service + rolling file
│   ├─ GitService         branch, dirty, ahead/behind, worktrees
│   └─ StateStore         desired state, for restore after restart
└─ HTTP API (ASP.NET Core, Kestrel on 127.0.0.1:4000)
    ├─ REST /api/...
    ├─ SSE or WebSocket for live logs and status events
    └─ static React UI (embedded resources)

devm.exe  (CLI)
├─ commands → HTTP API
├─ `devm mcp`  → MCP server over stdio → HTTP API
└─ `devm hook` → Claude Code PreToolUse hook
   Starts `RepoManager.exe --background` when the daemon is not running.
```

### 4.1 Solution layout

| Project | Purpose |
|---|---|
| `src/RepoManager.Core` | All services above. No UI or HTTP dependencies. Unit-testable. |
| `src/RepoManager.App` | WinForms shell, tray, WebView2 window, Kestrel host, API endpoints. |
| `src/RepoManager.Cli` | `devm` CLI, MCP server, hook. |
| `src/RepoManager.Contracts` | API DTOs shared by App and Cli. |
| `web/` | React + Vite + TypeScript UI. Build output embedded into App. |
| `tests/` | xUnit tests for Core, integration tests, CLI/MCP tests, fake server tool. |

Target framework: .NET 10 (LTS). Publish: self-contained, single-file, `win-x64`.

### 4.2 Security

- API binds to `127.0.0.1` only.
- On first start the daemon writes a random token to `%LOCALAPPDATA%\RepoManager\token` (user-only ACL). Every API request must send it in the `X-RepoManager-Token` header. The CLI reads the file; the WebView2 window gets the token injected by the host.
- This blocks web pages in the user's browser from calling `localhost:4000` and starting processes.

### 4.3 Data location

`%LOCALAPPDATA%\RepoManager\`:
- `registry.json` — central config layer (§5.3)
- `state.json` — desired state and sticky worktree ports
- `logs\<instance>\<service>.log` — rolling log files
- `token`

## 5. Configuration model

### 5.1 Identifiers

- Service: `<project>/<service>`, e.g. `panel-pro/api`.
- Worktree instance: `<project>@<worktree>/<service>`, e.g. `panel-pro@feat-x/api`. `<worktree>` is the worktree folder name.
- The main checkout is the instance without a suffix.

### 5.2 Layers

Three layers, merged field by field per service. Higher layer wins: 3 > 2 > 1.

**Layer 1 — `.claude/launch.json`** in the repo. Read as-is, never written by RepoManager. Mapping: `name` → service name, `runtimeExecutable` + `runtimeArgs` → command and args, `port`, `autoPort`, `url`.

**Layer 2 — `devservers.json`** in the repo root. Optional, meant to be committed.

```json
{
  "services": {
    "api": {
      "cwd": ".",
      "env": { "ASPNETCORE_URLS": "http://127.0.0.1:${port:api}" },
      "health": "http://127.0.0.1:${port:api}/health",
      "autoRestart": false
    },
    "web": {
      "command": "npm",
      "args": ["run", "dev", "--prefix", "web", "--", "--port", "${port:web}", "--strictPort"],
      "dependsOn": ["api"],
      "prepare": [{ "if": "!exists(web/node_modules)", "run": "npm ci --prefix web" }],
      "autoPort": true,
      "readyTimeoutSeconds": 120
    }
  },
  "tasks": {
    "publish": { "command": "publish.bat" },
    "test": { "command": "dotnet", "args": ["test"] }
  }
}
```

Service fields: `command`, `args`, `cwd`, `env`, `port`, `autoPort`, `url`, `health`, `dependsOn`, `prepare`, `autoRestart`, `readyTimeoutSeconds`, `disabled`. Services merge by name onto layer 1; new names are allowed. `prepare` steps run in order before start; `if` supports `exists(path)` and `!exists(path)`.

Task fields: `command`, `args`, `cwd`, `env`.

**Layer 3 — central `registry.json`**: list of registered repo paths, groups/tags per project, per-machine overrides in the same shape as layer 2 (services and tasks), notification toggles. Repos without layer 1 or 2 are defined entirely here through the UI.

### 5.3 Merge rules

- Scalars: highest layer that sets the field wins.
- `env`: merged by key.
- `args`, `prepare`, `dependsOn`: replaced as a whole, not merged.
- The UI shows which layer each effective value comes from.

### 5.4 Variables

Resolved at start time, after ports are decided:
- `${port:<service>}` — effective port of a service in the same instance.
- `${root}` — instance root folder (main checkout or worktree path).
- `${env:NAME}` — environment variable of the daemon.

### 5.5 Validation

Runs on load and on every file change (file watcher). Errors: invalid JSON, unknown `dependsOn` target, dependency cycle, unknown variable, the same fixed port used by two services on the machine. A project with errors is marked invalid and cannot start; other projects are not affected. Errors show in the UI banner and in `devm status`.

### 5.6 Discovery

- Add project: pick a folder.
- Scan: pick a folder (e.g. `source\repos`), list repos that contain `.claude/launch.json`, `devservers.json`, `package.json` or `*.csproj`, add selected ones in bulk.
- No command guessing. Repos without layer 1 or 2 need services defined in the UI.

## 6. Process lifecycle

### 6.1 Spawn

- Run as `cmd.exe /c <command> <args>` so `npm.cmd`, `.bat` and `.cmd` work.
- Working directory = resolved `cwd` relative to the instance root.
- Environment = daemon environment + merged `env` + `FORCE_COLOR=1`.
- stdout and stderr captured through pipes. No ConPTY. The UI renders ANSI colors.

### 6.2 Job Objects

- The daemon creates a root Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. If the daemon exits or crashes, Windows kills every process in it.
- Each service runs in its own nested job. Stop = terminate that job, which kills the full tree (`dotnet run` → `PanelPro.Api.exe`, `npm` → `node`).
- Stop is immediate termination. Dev servers do not need a graceful shutdown.

### 6.3 States

`Stopped → Preparing → WaitingDeps → Starting → Running ⇄ Unhealthy → Stopping → Stopped`, plus `Crashed` (process exited while Running) and `Failed` (could not start, or crash limit hit).

### 6.4 Ready detection

In order of preference: `health` URL returns 2xx; else the port is listening; else the process is still alive after 3 s. If not ready within `readyTimeoutSeconds` (default 120) → `Failed`.

Health polling while Running: every 10 s when `health` is set. Non-2xx or timeout 3 times in a row → `Unhealthy`. Recovery → `Running`.

### 6.5 Dependencies

Start of a service starts its `dependsOn` services first and waits until they are `Running`. Stopping a dependency warns about running dependents and does not stop them.

### 6.6 Port conflicts

Before start, every port of the service is checked.
- Busy, owned by a managed service → error "already running as `<id>`".
- Busy, owned by an external process → error with PID, process name and command line (owner found via `GetExtendedTcpTable`). `devm start <svc> --kill-owner` or the UI button kills the owner tree and continues.

### 6.7 Auto restart

Opt-in per service (`autoRestart: true`). Backoff 1 s, 2 s, 4 s, 8 s, 16 s. Five crashes within 60 s → `Failed` and a notification.

### 6.8 Logs

- Per service: in-memory ring buffer of 5000 lines plus a rolling file of 10 MB × 3.
- Each line has a timestamp and a stream marker (stdout/stderr).
- Queries: tail N, since duration, grep substring.
- Logs are kept after the service stops, so the crash output is readable.
- Prepare steps and tasks log into the same store.

### 6.9 Worktrees

- `git worktree list --porcelain` per registered repo, refreshed when the UI opens and every 30 s.
- A worktree instance uses auto-assigned ports for all services, from range 20000–29999. Ports are sticky per instance (saved in `state.json`).
- A service with a fixed `port` and no `${port:...}` reference in its args or env is "not worktree-ready". Starting it in a worktree is refused unless `--force` is given. The UI shows the warning with the fix (read the port from an env variable in `launchSettings.json` / `vite.config.ts`).

### 6.10 Port assignment in the main checkout

Fixed port from config by default. `autoPort: true` → use the configured port when free, else the next free port in 20000–29999. The effective port is shown in UI, CLI and MCP and substituted into `${port:...}`.

### 6.11 Restore

`state.json` stores which instances and services the user wants running. On daemon start, those are started again (respecting `dependsOn`). A user stop removes the service from the desired state; a crash does not.

### 6.12 Tasks

Same spawn, job and log handling, but run to exit and report the exit code. One run per task at a time; a second run request while one is active returns an error.

## 7. Claude integration

### 7.1 CLI (`devm`)

Resolves the project and instance from the current directory (main checkout or worktree), so short service names work inside a repo.

```
devm status [--all]
devm start|stop|restart <svc|all> [--kill-owner] [--force]
devm wait <svc> [--timeout 120]
devm logs <svc> [--tail N] [--since 5m] [--grep text] [--follow]
devm run <task>
devm url <svc>
devm ports
devm add <path>
devm scan <dir>
devm mcp
devm hook
```

Every command accepts `--json`. Exit codes: 0 success, 1 error, 2 service failed to start (the last 50 log lines are printed).

If the daemon is not running, commands other than `hook` start `RepoManager.exe --background` and wait up to 10 s for the API.

### 7.2 MCP server (`devm mcp`, stdio)

Tools: `list_services`, `service_status`, `start_service`, `stop_service`, `restart_service`, `get_logs`, `run_task`, `get_url`. Each tool accepts an optional `cwd` for project resolution. Failed starts return the reason and the log tail. Built with the official C# MCP SDK.

### 7.3 PreToolUse hook (`devm hook`)

Reads the hook JSON from stdin and returns allow or deny with a message.

Deny when:
- A Bash/PowerShell command kills a managed process: `taskkill`, `Stop-Process`, `kill`, `npx kill-port` and similar, targeting a managed PID, a managed port, or a bare process name such as `node` or `dotnet`. Message names the service and the `devm` command to use instead.
- A Bash/PowerShell command starts a managed service's command directly (e.g. `npm run dev`, `dotnet run --project ...`) inside a managed repo or worktree. Message suggests `devm start <svc>`.
- `mcp__Claude_Browser__preview_start` is called with a `name` that matches a managed service. Claude desktop reads the same `.claude/launch.json` and would spawn a second copy. Message suggests `devm start <svc>` and then `preview_start` with the `url` of the running instance.

Always allow when:
- The daemon is not running.
- The match is uncertain.
- The hook itself errors or times out (hard limit 2 s).

The hook must never be the reason work is blocked when RepoManager is off or confused.

### 7.4 Global instructions

The installer adds a marked block to `~/.claude/CLAUDE.md` (removable by the uninstaller): use `devm` for dev servers, read logs with `devm logs`, never kill or start dev servers directly.

## 8. Desktop app

### 8.1 Window

WinForms form hosting WebView2, which loads the embedded React UI from the local API.

- Left sidebar: projects grouped by tag; each expands to main checkout and worktrees; status dot per service.
- Project view: services table (state, port with URL link, uptime, restart count, health), start/stop/restart per service, start all/stop all, tasks with run button and last exit code, git bar (branch, dirty, ahead/behind), quick actions (VS Code, Rider, Explorer, terminal).
- Log pane: live, ANSI colors, search, pause, merged view of several services.
- Ports view: every listening TCP port on the machine, owner process, managed or not.
- Config view: effective config per service with origin layer per field; editor for layer 3 overrides.
- Groups: start/stop all projects in a group.
- Config errors banner.

If the WebView2 runtime is missing, the app opens the UI in the default browser instead.

### 8.2 Tray

- Icon color shows overall state: all fine / something unhealthy / something failed.
- Menu: Open, running services (each with Stop), Start group ▸, Quit.
- Closing the window hides it to the tray. Services keep running.
- Quit asks for confirmation when services are running, then stops everything.

### 8.3 Notifications

Windows toast on crash, on `Failed`, and on `Unhealthy` longer than 30 s. Toggle per project.

### 8.4 Install and start

`install.ps1`:
- Publishes or copies `RepoManager.exe` and `devm.exe` to `%LOCALAPPDATA%\RepoManager\bin`.
- Creates a Start Menu shortcut.
- Optionally registers autostart at login (Task Scheduler, starts with `--background`, tray only).
- Adds the `bin` folder to the user `PATH`.
- Registers the MCP server (`claude mcp add --scope user devm -- devm mcp`) and the PreToolUse hook in `~/.claude/settings.json`, after taking a backup of the file.
- Adds the `CLAUDE.md` block (§7.4).
- Rerunning the script updates in place. `uninstall.ps1` reverts every step.

Start modes: autostart at login, Start Menu shortcut, and on demand from the first `devm` command. A second launch of `RepoManager.exe` activates the existing window (named mutex + message).

## 9. Error handling

- A config error affects only its project.
- Every failed start returns a reason and the last log lines, identically in UI, CLI and MCP.
- Daemon crash: Job Object kills all children; the next start restores from `state.json`.
- API unreachable from CLI: start the daemon, retry, then fail with a clear message.
- Hook: fail open (§7.3).

## 10. Testing

- Core unit tests: config merge, variable resolution, validation, dependency ordering, port allocator, state transitions, hook command matching (table of real commands → allow/deny).
- Integration tests with a small fake server tool (listens on a port, prints lines, crashes on demand, spawns a child): start, stop, tree kill, no orphans after stop and after daemon kill, ready detection, health, restart backoff, port conflict owner lookup.
- CLI and MCP tests against an in-process daemon.
- UI component tests for the main views; manual checks in the real window.
- Dogfooding on the 6 existing repos from M1 onward.

## 11. Milestones

Each milestone gets its own implementation plan and is usable on its own.

- **M1 Core** — solution skeleton, daemon hosted headless (`RepoManager.exe --background` with no window yet), config layers 1–3, validation, start/stop/restart, Job Objects, dependsOn, prepare, ready detection, fixed ports + conflict detection + owner lookup, `autoPort` in the main checkout (§6.10) with `${port:...}` resolution, logs, state restore, token auth, `devm` CLI.
- **M2 Claude** — MCP server, PreToolUse hook, `CLAUDE.md` block, registration steps of the installer.
- **M3 App** — React UI, WinForms + WebView2 window, tray, git info, quick actions, ports view, config view, full `install.ps1` / `uninstall.ps1`, single-file publish.
- **M4 Worktrees** — worktree detection, forced auto-port in worktrees, auto-assigned sticky ports, worktree readiness check, per-worktree instances in UI/CLI/MCP.
- **M5 Extras** — groups/tags, health polling + Unhealthy state, auto restart with backoff, toast notifications, tasks.

After M1 and M2 the main pain (Claude killing servers, orphans) is solved.

## 12. Future (not planned)

- Terminal UI client over the same API.
- ConPTY support for tools that need a real terminal.
