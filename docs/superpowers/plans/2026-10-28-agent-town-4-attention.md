# Agent Town, Plan 4: Attention and settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the town is not on screen, RepoManager gets the user's attention for sessions that need them (tray blink, Windows notification, sound) and quietly marks finished ones, with settings for each, and an "Agent Town" setting that decides whether the town hooks are installed.

**Architecture:** A pure `AttentionTracker` in Core turns session changes and town focus into decisions (notify, play a sound, blink, done badge). The WinForms `TrayApp` carries them out with `NotifyIcon` (blink, balloon notifications) and `SoundPlayer` (WAV beeps synthesised in memory). Settings live in `registry.json` under `town`, behind `GET/POST /api/town/settings`; `devm claude-setup` reads the hook setting. The web town gets a settings panel and a sound button, and opens a session's box when the shell asks (notification click for a terminal session).

**Tech Stack:** .NET 10 (WinForms, ASP.NET Core minimal APIs), xUnit; React 19, TypeScript, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-27-agent-town-design.md` (sections "Attention when the town is not focused", "Hook cost", "Built so far").

**Series:** Plan 4 of 5. Plans 1 to 3 are merged. Focus reporting (5 s heartbeat, 15 s lease, `PermissionBroker.Focused`) already exists. Plan 5: game elements.

## Global Constraints

- Nothing may block or delay real work: attention is only ever extra. A failed notification or sound is ignored silently.
- Needs you (a session enters `waiting`): tray icon blinks amber, a Windows notification ("kodex: wants to run npm install"), a short sound. Clicking the notification opens that session's own window; for a session whose window cannot be opened (terminal not found), it opens the town with that worker's dialogue.
- Done (a session enters `done`): tray icon and a softer sound. No Windows notification.
- Tray blinking stops when nothing needs the user.
- While the town is focused (`PermissionBroker.Focused`): no notifications and no tray blink, only in-game sounds and animation.
- Settings: tray, notifications and sound each switch on and off; sound has its own mute (the speaker button in the town's top bar). "Agent Town" setting controls whether `claude-setup` installs the town hooks; on by default.
- Notification text, exact: permission prompt with a Bash/PowerShell command: `<lot>: wants to run <command>`; other tools: `<lot>: wants to use <Tool>`; a question (waiting, no prompt): `<lot>: has a question`; the notification body is the session's message (clipped to 200 characters). `<lot>` is the session's project, else its folder.
- Sounds: needs-you = two square-wave notes (880 Hz then 1320 Hz, 90 ms each, volume 0.25); done = one softer sine note (660 Hz, 160 ms, volume 0.15). 22 050 Hz mono 16-bit PCM WAV, built in memory.
- Settings persist per machine in `registry.json` (`town` object); defaults all on.

## Review Focus

1. **A burst of events** (a session flips waiting → working → waiting within a second, or ten sessions finish together). Expected: at most one notification per session per wait, one sound per 2 s at most (rate limit), no balloon storm. Test in Task 2.
2. **Focus changes while something waits.** Expected: town focused → blink stops at once; town loses focus while a session still waits → blinking resumes, but no second notification for the same wait. Test in Task 2.
3. **Session ends while it needs the user.** Expected: it leaves the needs-you set; blinking stops if it was the last. Test in Task 2.
4. **Hooks setting turned off from the town.** Expected: the town hooks are removed from `~/.claude/settings.json` (backup first, as `claude-setup` does), the dev-server hook and the user's own hooks stay, and a later `install.ps1` (`claude-setup`) does not add them back. Test in Task 1.
5. **Notification click after the session is gone.** Expected: nothing happens beyond opening RepoManager; no crash. Test in Task 3 (`TargetFor` returns null).

---

## File Structure

- Modify `src/RepoManager.Core/Config/Models.cs` (`TownSettings` in `RegistryFile`), `ConfigService.cs` (`UpdateTownSettings`).
- Modify `src/RepoManager.Contracts/AgentDtos.cs`: `TownSettingsDto`.
- Modify `src/RepoManager.App/Daemon/Api.cs`: `GET/POST /api/town/settings`; hook toggle runs `devm claude-setup`.
- Modify `src/RepoManager.Cli/ClaudeIntegration.cs`, `Cli.cs`: `claude-setup` reads the hook setting.
- Create `src/RepoManager.Core/Agents/AttentionTracker.cs`, `src/RepoManager.Core/Agents/Beeps.cs`.
- Modify `src/RepoManager.App/Shell/TrayApp.cs`, `Icons.cs`, `MainForm.cs`.
- Web: `web/src/api/types.ts`, `client.ts`, `lib/host.ts`, `App.tsx`, `views/TownView.tsx`, create `views/town/TownSettings.tsx`, `styles.css`.
- Tests: `tests/RepoManager.Tests/AttentionTrackerTests.cs`, `BeepsTests.cs`, additions to `TownApiTests.cs`, `ClaudeSetupTests.cs`; web tests next to the code.

---

### Task 1: Town settings and the hook setting

**Files:** Models.cs, ConfigService.cs, AgentDtos.cs, Api.cs, ClaudeIntegration.cs, Cli.cs; tests in TownApiTests.cs, ClaudeSetupTests.cs.

**Interfaces:**
- Produces (Core): `sealed class TownSettings { bool Tray = true; bool Notifications = true; bool Sound = true; bool Hooks = true; }` as `RegistryFile.Town` (never null after load); `ConfigService.UpdateTownSettings(TownSettingsDto s): ActionResultDto` (saves).
- Produces (Contracts): `sealed record TownSettingsDto(bool Tray, bool Notifications, bool Sound, bool Hooks);`
- Produces (HTTP): `GET /api/town/settings` → `TownSettingsDto`; `POST /api/town/settings` body `TownSettingsDto` → `ActionResultDto`. When `Hooks` changes, the daemon runs `devm claude-setup --no-mcp` (on) or `devm claude-setup --no-mcp --no-town` (off), with `devm.exe` found next to the running `RepoManager.exe` or via `REPOMANAGER_DEVM`; failure is reported in the result message and the setting is still saved.
- Produces (CLI): `claude-setup` installs town hooks only when `--no-town` is absent **and** `registry.json`'s `town.hooks` is not `false` (read from `Protocol.DataDir()`); with the setting off it removes them, as `--no-town` does.

- [ ] **Step 1: Write failing tests:** `Settings_DefaultsAllOn`; `Settings_RoundTrip_PersistsInRegistry` (POST, then a new `ConfigService` on the same home reads them back); `ClaudeSetupTests.Setup_HooksSettingOff_RemovesAndSkipsTownHooks` (write `registry.json` with `"town":{"hooks":false}` into a temp `REPOMANAGER_HOME`, run `claude-setup --no-mcp`: no `town-hook` entries, dev-server and user hooks kept).
- [ ] **Step 2: Run** `dotnet test --filter "TownApiTests|ClaudeSetupTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Use `REPOMANAGER_DEVM` in tests to point at `CliTests.DevmExe`.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(town): town settings, and the Agent Town hook setting`.

### Task 2: Attention tracker

**Files:** Create `src/RepoManager.Core/Agents/AttentionTracker.cs`, `tests/RepoManager.Tests/AttentionTrackerTests.cs`.

**Interfaces:**
- Produces:
  - `sealed record AttentionNotice(string SessionId, string Title, string Body);`
  - `enum AttentionSound { NeedsYou, Done }`
  - `sealed class AttentionTracker(Func<DateTimeOffset> clock)` with
    - `AttentionUpdate OnSession(AgentSessionDto s, bool townFocused, TownSettings settings)`
    - `AttentionUpdate OnEnded(string sessionId, bool townFocused, TownSettings settings)`
    - `AttentionUpdate OnFocus(bool townFocused, TownSettings settings)`
    - `void Seen()` (clears the done badge; called when the town gains focus)
  - `sealed record AttentionUpdate(AttentionNotice? Notice, AttentionSound? Sound, bool Blink, bool DoneBadge);`
- Rules: a session *entering* `waiting` (from any other state, or first seen waiting) → notice (if `settings.Notifications && !focused`) and `NeedsYou` sound (if `settings.Sound`); staying `waiting` produces nothing new. Entering `done` → `Done` sound and `DoneBadge = true` while not focused. `Blink` = `settings.Tray && !focused && anyWaiting`. Sounds rate-limited to one per 2 s (later ones in the window are dropped). Focus true → `Seen()` semantics (badge cleared). Notice text per Global Constraints (`lot` = `Project ?? Folder`; command from the first prompt's `Summary` when `ToolName` is Bash/PowerShell).
- [ ] **Step 1: Write failing tests** covering: enter waiting notifies once (Review Focus 1); repeated waiting events do not; waiting → working → waiting notifies again; focused → no notice, sound still plays, no blink (Review Focus 2); focus lost with a session still waiting → blink true, no notice; session ended → leaves the set, blink false (Review Focus 3); done → soft sound and badge, badge cleared on focus; sound rate limit (two sessions within 1 s → one sound); settings off → no notice/sound/blink respectively; exact notice titles for Bash, Write and a question.
- [ ] **Step 2: Run** `dotnet test --filter AttentionTrackerTests`. Expected: FAIL.
- [ ] **Step 3: Implement** (thread-safe with a lock; no I/O).
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): decide when to get the user's attention`.

### Task 3: Tray, notifications and sounds in the shell

**Files:** Create `src/RepoManager.Core/Agents/Beeps.cs` (+ `BeepsTests.cs`); modify `TrayApp.cs`, `Icons.cs`, `MainForm.cs`, `Api.cs` (if needed for the open logic).

**Interfaces:**
- Produces: `static byte[] Beeps.Wav(AttentionSound sound)` (values in Global Constraints); `Icons.Tray(Status status, Overlay overlay)` with `enum Overlay { None, NeedsYou, Done }` (amber or green corner dot).
- TrayApp subscribes to `Agents.Changed`, `Agents.Ended` and a new `PermissionBroker.FocusChanged` event (raised on every change of `Focused`, including lease expiry), feeds `AttentionTracker`, and on the UI thread: shows `ShowBalloonTip` for a notice (remembering its session id for `BalloonTipClicked`), plays sounds with `SoundPlayer` on a `MemoryStream`, blinks with a 500 ms timer alternating the amber overlay, shows the done overlay while `DoneBadge`.
- `static string? TrayTarget.TargetFor(string? sessionId, AgentRegistry agents)` returns the id when the session still exists (Review Focus 5). Click: if `/town/open` logic (extract to `AgentWindows.Open(DaemonHost, string sessionId)`) succeeds, done; otherwise show the RepoManager window and post `{ type: 'openSession', id }` to the page.
- [ ] **Step 1: Write failing tests:** `Beeps_WavHeaderAndLength` (RIFF/WAVE, 22050 Hz, 16-bit mono, data length = samples × 2 for the exact note lengths); `TargetFor_GoneSession_Null`; `FocusChanged_RaisedOnLeaseExpiry` (broker with a short lease).
- [ ] **Step 2: Run** `dotnet test --filter "BeepsTests|PermissionBrokerTests|TownApiTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the shell wiring (no automated test for NotifyIcon; Task 5 checks it live).
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(town): tray blink, notifications and sounds`.

### Task 4: Settings panel, mute, and opening a session from the shell

**Files:** Create `web/src/views/town/TownSettings.tsx` (+ test); modify `web/src/api/types.ts`, `client.ts`, `lib/host.ts`, `App.tsx`, `views/TownView.tsx` (+ test), `styles.css`.

**Interfaces:**
- Produces (client): `api.townSettings(): Promise<TownSettingsDto>`, `api.saveTownSettings(s): Promise<ActionResultDto>`.
- Produces (host.ts): `onShellCommand(cb: (cmd: { type: 'openSession'; id: string }) => void): () => void`.
- TownView: a gear button opens `TownSettings` (four switches: "Blink the tray icon", "Windows notifications", "Sounds", "Agent Town hooks in Claude Code" with the note "Off removes the town hooks from Claude Code's settings"); a speaker button in the top bar toggles `sound` (aria-pressed); `TownView` takes an optional `openSessionId` prop and opens that box on mount/change.
- App: `onShellCommand` → switch to town mode and pass `openSessionId`.
- [ ] **Step 1: Write failing tests:** settings panel loads, toggling a switch saves the whole DTO; the speaker button toggles sound and saves; a failed save shows its message; App switches to town and opens the box on `openSession`.
- [ ] **Step 2: Run** `npx vitest run src/views src/App.test.tsx`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npx vitest run`, `npx tsc -b`. Expected: pass.
- [ ] **Step 5: Commit** `feat(town): attention settings, mute, open a session from a notification`.

### Task 5: Live check

- [ ] **Step 1:** Ask the user to approve reinstalling (`install.ps1`; restarts RepoManager). Do not proceed without approval.
- [ ] **Step 2:** With RepoManager minimised, have a session ask for permission: the tray blinks amber, a notification "panel-pro: wants to run …" appears with a sound; clicking it opens the Claude session; blinking stops once answered.
- [ ] **Step 3:** With the town focused: no notification, no blink, sound only.
- [ ] **Step 4:** A session finishes while RepoManager is minimised: soft sound and a green dot on the tray icon; opening the town clears it.
- [ ] **Step 5:** Toggle "Agent Town hooks" off and on in the settings panel; check `~/.claude/settings.json` each time (backup files appear).
- [ ] **Step 6:** Report results; no commit.
