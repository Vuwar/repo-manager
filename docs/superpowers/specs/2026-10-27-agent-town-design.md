# Agent Town design

Date: 2026-10-27 (updated 2026-10-28 with what was decided while building plans 1 to 3; updated 2026-10-29 with past sessions and new sessions)
Status: plans 1 to 5 built; plan 6 (past sessions and new sessions) to do

## Intent

The user runs many Claude Code sessions at once, across many repos, mostly in the Claude desktop app and sometimes in a terminal. Agent Town is a full-screen screen inside RepoManager that shows every session as a pixel-art worker in front of its repo's building, and lets the user answer and instruct those sessions from inside the town, in the style of an RPG.

It started from a standalone prototype (a canvas page, a Node server and a `hook.js`), copied to `docs/superpowers/specs/agent-town/prototype/`. The approved mockups are in `docs/superpowers/specs/agent-town/` (`layout-30-repos.html`, `chat-box-v2.html`); they are content fragments from the brainstorm tool, so they render without its frame styles but their own `<style>` blocks carry the look. Agent Town keeps the prototype's look and states, but replaces its server with the RepoManager daemon, and adds talking to sessions, game elements and notifications.

The guiding rule, in the user's words: nothing should block or delay real work. This is for fun while doing the work.

## Goals

- See at a glance, full screen, which sessions need the user, which are working, which are done.
- Scale to around 30 repos.
- Answer permission prompts from the town.
- Send a session its next instruction from the town, at any time while the session is open, for both desktop and terminal sessions.
- Feel like a small RPG: dialogue boxes, a player character, XP and levels, a quest log.
- Get the user's attention when the town is not on screen.
- Reread past sessions (desktop and terminal) without starting Claude or spending tokens.
- Pick up a past session: desktop sessions resume in the desktop app, terminal sessions continue in a new terminal session with a handoff.
- Start a new session in a repo, in a terminal or in the desktop app, chosen each time.

## Non-goals

- Replacing the session windows. The town is an extra way in, never the only one.
- Remote sessions (other machines, cloud). Same machine only.
- Answering `AskUserQuestion` / elicitation dialogs from the town (v1 shows the question and an "Open window" button).
- Resuming a terminal session in place (`claude --resume`): it reloads the whole old conversation and costs tokens. Terminal sessions are continued with a handoff instead.
- Deleting, renaming or archiving sessions from the town.

## Anti-goals (failure even if it works)

- Any game element that blocks or delays real work: cooldowns, energy, penalties, forced walking.
- A session window that looks stuck because the town is holding it.
- Typing in a session window that gets swallowed or delayed because of the town.
- The messaging token written to disk, logs or the browser.

## Decisions made with the user

### Layout: one long scrolling street (option B, sorted)

- Every RepoManager project is a building on one horizontal street. Projects with no Claude session are included, smaller and dimmer, with lit windows while any of their dev servers runs.
- Order, left to right: repos with a session that needs the user, then working, then done, then idle sessions, then repos with no session. Within a group, longest-waiting first.
- Re-sorting is deferred while the pointer is over the town; it applies about 3 seconds after the pointer leaves or immediately on Home. (Claude's call.)
- The camera starts at the left end. Pan by drag, wheel and arrow keys; Home returns to the left end.
- A minimap strip at the bottom shows one dot per repo, coloured by state; clicking a dot jumps there. Edge arrows show off-screen counts such as "2 need you →".
- Each session is a worker in front of its repo's building. Several sessions in a repo means several workers. A worktree session stands at the main repo's building with a small sign showing the worktree name. (Claude's call; the user accepted the picture.)
- The window title shows "(N) RepoManager" when N sessions need the user.
- States and visuals are the prototype's: needs you (waves at the door, `!` bubble, amber beacon), working (hammer at anvil, smoke), done (jump, confetti, green), failed (roof on fire), idle (sleeps). Day and night cycle stays.

### Dialogue box (RPG style)

Mockup approved: `chat-box-v2.html` (kept with the brainstorm files; described here so the doc stands alone).

- Opens by clicking a worker, Enter next to it, Tab (cycles workers that need the user), or a quest in the quest log.
- Bottom-of-screen box: large pixel portrait of the worker on the left (its hat and shirt colours, with a mood badge `!` or `✓`), a name tag on the frame coloured by state ("KODEX · Session A"), text that types out letter by letter with a blinking ▼ for more. The town dims slightly behind it. Esc closes it ("walk away").
- **Permission prompt**: the worker speaks in first person, generated by the town ("Boss! I need to run a command first: `npm install` Can I?"). A small choice menu: "Go ahead / Always OK / No, don't", with arrow keys and Enter.
- **Done**: shows Claude's actual last message unchanged, and a `>` input line in the box. Enter sends, Shift+Enter makes a new line.
- **Working**: shows the current prompt and recent tools, plus the same `>` input line. A message sent here reaches Claude between tool calls (inbox behaviour). (Claude's call; resolves the earlier "read-only" idea in favour of the "at any time" goal.)
- **Waiting on a question (elicitation)**: shows the question and "Open window".
- Every box has an "Open window" action that opens that session in its own window where possible.

### Sending instructions: the session inbox, no holding

Tested on 2026-10-27 against a desktop session (Claude Code 2.1.308, desktop app 3.6556.2): a message posted from an outside process into an idle session's inbox started a new turn, and the session answered.

- Claude Code gives each session an inbox (named pipe on Windows) and exports `CLAUDE_CODE_MESSAGING_SOCKET` and `CLAUDE_CODE_MESSAGING_TOKEN` to hooks, before `SessionStart` runs.
- The `SessionStart` hook sends both to the daemon. The daemon keeps them in memory only, keyed by session id.
- To send, the daemon connects to the pipe and writes two lines, then closes:
  `{"type":"auth","token":"<token>"}` then `{"type":"user","message":{"role":"user","content":"<text>"}}`.
  This format came from strings in the Claude Code binary; it is not documented for outside apps.
- If the session is working, Claude reads it between tool calls; if idle, it starts a new turn.
- Claude sees it as "Another Claude session sent a message". It acts on it but will not treat it as the user's approval for anything. That is acceptable because permissions use a separate path.
- The `Stop` hook never waits. Earlier ideas of holding finished sessions are dropped.
- If sending fails (session gone, pipe refused, format changed), the box says so and offers "Open window". The typed text is kept so it can be copied.
- Risk: undocumented. A Claude Code update may break it. The fallback must keep the town usable.

### Permission prompts: answer from the town without blocking the window

- Uses the `PermissionRequest` hook, which can return allow or deny.
- The hook waits for a town answer only while the town window is focused. When the town loses focus (or is closed), every waiting hook returns "no decision" at once and the normal permission dialog appears in the session window.
- A prompt that arrives while the town is not focused gets "no decision" immediately; there is no grace wait (user's decision). In the town it shows as "Answer in the window" with "Open window", and still appears on the street and in the quest log.
- Whichever comes first wins. If the user answers in the session window, the town removes the prompt when the next hook event for that session arrives.
- "Always OK" returns the hook input's `permission_suggestions` as `updatedPermissions`, with every entry's `destination` forced to `session`, so the rule lasts for that session only and is never written to a settings file. With no suggestions it behaves as "Go ahead". "No, don't" denies with a message telling Claude the user declined it in Agent Town.
- Focus is a lease, not a flag: the town reports `focused:true` every 5 s while the main window has focus and is in town mode, and `false` at once when that stops (window deactivated, page hidden, town left). The daemon drops focus by itself 15 s after the last report, so a town that crashes or misses its "unfocused" message can never keep sessions waiting. Focus reports are sent in order.
- A prompt released by focus loss or the time-out stays listed as "Answer in the window" until the session's next non-notification event. Answering it from the town afterwards says "Already answered in the window." A double click sends one answer.
- Timeout on the hook itself is long (several minutes) but the focus rule ends it much sooner in practice.

### State rules (Claude's calls)

- Needs you: a permission prompt or elicitation is pending. Counts in the title "(N)", blinks the tray, is a quest.
- Done: stays done until the next `UserPromptSubmit` (no timeout to idle). Is a lower-priority quest ("Review ai-shell's result"), sorted after needs-you quests. Does not count in "(N)". Opening its dialogue marks it seen; seen done sessions drop off the quest log but stay done on the street.
- Idle: session started, no prompt yet.
- A session whose folder is not a RepoManager project gets a temporary building named after the folder, marked "not in RepoManager", placed by the same sort rules. It disappears when its last session ends.

### Town window (Claude's call)

The town is a fourth `UiMode` of the existing main window, not a separate window. Entering town mode maximises the window (F11 toggles borderless full screen, Esc with nothing open leaves it); leaving restores the previous bounds, like the Mini view does. "Town focused" means the main window has focus and is in town mode. The scene is drawn at a whole number of device pixels per scene pixel, so it stays crisp at 125% and 150% display scaling.

### Hook cost (Claude's call)

Hooks fire on every tool call in every session, so they must not slow sessions down.
- An "Agent Town" setting controls whether `claude-setup` installs the town hooks; on by default.
- Status hooks (SessionStart, UserPromptSubmit, PostToolUse, Notification, Stop, StopFailure, SessionEnd) are fire-and-forget: they give up after 1 s, always exit 0 and print nothing. All of them run with Claude Code's `async: true` option except SessionEnd, which runs in line so it arrives before the session exits. Async hooks are not ordered, so an event that arrives up to 2 minutes after SessionEnd cannot bring the session back.
- Only `PermissionRequest` may wait, and only under the rule in the permission section.

### Game elements

Rule: every game action has a fast path.

- **Player character**: a "boss" on the street, walked with arrow keys. Enter next to a worker opens its dialogue. Clicking a worker opens the dialogue immediately and the boss runs over in the background.
- **Player XP**: for answering a permission prompt (more when answered quickly) and for sending an instruction that gets a session working again.
- **Repo XP and levels**: a repo earns XP when its sessions finish turns. Buildings grow with level (shack, house, tower) and gain small decorations.
- Workers do not level; sessions are short-lived.
- XP is stored in the RepoManager data directory and survives restarts.
- **Quest log** (key Q): everything waiting on the user, with how long it has waited. Clicking a quest opens that dialogue. Completing one plays a reward sound and shows an XP pop-up.

### Attention when the town is not focused

- Needs you: tray icon blinks amber, Windows notification ("kodex: wants to run npm install"), short sound. Clicking the notification opens that session's own window (user's decision), because the prompt can only be answered there. For a terminal session that cannot be brought forward, it opens the town with that worker's dialogue instead.
- Done: tray icon and a softer sound. No Windows notification.
- Tray blinking stops when nothing needs the user.
- While the town is focused: no notifications, only in-game sounds and animation.
- Settings: tray, notifications and sound each switch on and off; sound has its own mute.
- The Claude desktop app has its own notifications, so the user may get duplicates; they can turn either off.

## How it fits RepoManager

Findings from reading the code (2026-10-27):

- Daemon: ASP.NET Core minimal APIs on Kestrel, loopback only, port 4000 by default (`src/RepoManager.App/Daemon/DaemonHost.cs`). Host check plus `X-RepoManager-Token` on `/api` (`Api.cs:18-38`).
- Live updates: SSE at `GET /api/events` (`Api.cs:156`), fanned out by `EventHub` (`EventHub.cs`). `Publish` is private today; session events need a way in, and a new event type (for example `agent`).
- Long-held requests already exist (`GET /api/services/wait`, `Api.cs:74`), a precedent for the waiting `PermissionRequest` call.
- Repo mapping: `ConfigService.InstanceForPath(cwd)` (`ConfigService.cs:63`) maps a hook's `cwd` to a project and worktree instance. Use it to place each session.
- Hooks: `devm hook` already relays a PreToolUse hook to the daemon (`src/RepoManager.Cli/Hook.cs`) and fails open with a 2 s timeout. The town needs its own hook command (for example `devm town-hook`) with its own timeouts. `ClaudeIntegration.cs` installs hooks into `~/.claude/settings.json` and recognises its own by `IsOurHook`; it needs add and remove logic for the new events. `install.ps1` and `uninstall.ps1` already call `claude-setup` and `claude-remove`.
- Surface: hooks see `CLAUDE_CODE_ENTRYPOINT` (`claude-desktop` in desktop sessions; the terminal value is to be confirmed) and, in desktop, `CLAUDE_CODE_HOST_SESSION_ID` (the desktop app's session id).
- Web UI: Vite + React, screens chosen by `UiMode` in `web/src/lib/simple.ts` and switched in `App.tsx`. The town is a new mode `town`. State in `web/src/state`, SSE client in `web/src/api/sse.ts`. Tests are Vitest next to the code.
- Shell: `MainForm.cs` hosts WebView2 and handles UI messages (`OnUiMessage`) for window modes. The town needs a full-screen or maximised window, focus reporting to the daemon (for the permission rule), tray blinking (`TrayApp`) and Windows notifications.

### Hook events used

| Hook | Town effect |
| --- | --- |
| SessionStart | worker appears (idle); inbox address and token registered |
| UserPromptSubmit | working; prompt text shown |
| PostToolUse | working; tool name shown. For the desktop app's `mcp__ccd_turn__wrap_up` tool, its recap, question and reply buttons are kept until the next prompt |
| PermissionRequest | needs you; waits for a town answer while the town is focused |
| Notification (permission, elicitation types) | needs you |
| Stop | done; the whole last turn shown (read from the transcript); never waits |
| StopFailure | failed (the turn ended on an API error); the error text shown; stays until the next prompt |
| SessionEnd | worker walks away; inbox forgotten |

Failed state: Claude Code's `StopFailure` hook reports a turn that ended on an API error (rate limit, overload, authentication and so on); it maps to failed.

## Existing uncommitted work

The working tree has uncommitted Mini view work (`MiniView.tsx`, `ambient.ts`, `host.ts`, `ScreenSampler.cs`, changes to `App.tsx`, `simple.ts`, `MainForm.cs`, `styles.css`, `SimpleView.tsx`, `Icon.tsx`). It touches the same mode switch, shell messages and styles. It must be committed (or set aside by the user) before Agent Town work starts. The town should reuse `host.ts` for shell messages.

## Built so far, and decisions made while building (2026-10-28)

Plans 1 to 3 are built and pushed: the backend (hooks, registry, inbox, permission broker, `/api/town/*`), the town screen (street, minimap, edge arrows, labels, title count) and the dialogue (box, permission menu, sending, quest log, "Open window"). These points were decided along the way; not all of them are in the sections above.

- **Messages from the town say they come from the user.** Claude Code shows inbox messages as coming from "another Claude session", and a session then answered that other session instead of the user. Every town message now starts with "[From the user, typed in RepoManager Agent Town (their view of all sessions). Reply here in this session, as you would to them.]". It still grants no permission.
- **Open window.** A desktop session opens with `claude://code/continue?session=<CLAUDE_CODE_HOST_SESSION_ID>`, a link found in the desktop app's own handler (undocumented; the app checks the id against `^local_[A-Za-z0-9-]{1,64}$`). Otherwise RepoManager brings forward the window of the session's Claude process: the hook sends the nearest `claude*.exe` ancestor's pid, and the daemon walks up from it to the first process with a window, stopping at the desktop shell and system processes. The pid is stored with its start time, so a reused pid is never activated.
- **Session titles.** The name tag shows the desktop app's title for the session (the transcript's `custom-title` or `agent-name` line), else the first prompt shortened to 48 characters, instead of "Session A".
- **The whole last turn.** The Stop hook only carries the last assistant message, so the daemon reads the end of the session's transcript (only `<session id>.jsonl`, at most the last 1 MB) for every assistant message since the last prompt.
- **The wrap-up card.** The desktop app's end-of-turn card ("Commit / Not yet") is the MCP tool `mcp__ccd_turn__wrap_up` with `recap`, `ask` and `actions[{label, message}]`. The town shows it in the box with the same reply buttons (a button sends its message), and its question becomes the quest title.
- **Markdown.** Claude's replies are rendered as Markdown (headings, lists, code blocks, bold, italic, inline code) as React text, never HTML. Links show their text; links to other desktop sessions (`#local_...`) open that worker's box, or the session in Claude when it is not in the town.
- **Working box.** It shows the current prompt and the last five status messages; new messages extend the text without retyping it.
- **Keyboard.** Click, Tab (only when someone needs the user) and the quest log (Q) open a box; Esc closes it. Town keys never fire while typing in the box. A half-typed message survives the quest log.
- **Focus reporting** was planned for the attention phase but moved into plan 3, because the permission menu cannot work without it.
- **Not built yet:** workers walking in and away (plan 5), attention when the town is not focused (plan 4), the "Agent Town" setting in the app (today only `devm claude-setup --no-town`; plan 4), game elements (plan 5).

## Past sessions and new sessions (added 2026-10-29)

Asked for by the user: see older sessions to reread them and to pick up the work, and start a new session from the town, choosing Terminal or Desktop each time. Viewing must not spend tokens; the user named `claude --resume` in a terminal as the cost they want to avoid.

### What was found on this machine (2026-10-29, desktop app 4.29.0)

- Every Claude Code session, desktop or terminal, writes its transcript to `~/.claude/projects/<folder slug>/<session id>.jsonl`. Each line carries `entrypoint` (`cli` for terminal) and `cwd`. Title lines (`custom-title`, `agent-name`, `ai-title`) are already read by `TranscriptReader`.
- The desktop app keeps one file per session in `%APPDATA%\Claude\claude-code-sessions\<account>\<org>\local_<id>.json` with `sessionId` (`local_...`), `cliSessionId` (the transcript's id), `cwd`, `title`, `isArchived`, `createdAt` and `lastActivityAt` (epoch ms). This tells which transcripts came from the desktop and gives the id the `continue` link needs.
- The desktop app handles `claude://code/continue?session=local_...` (already used by "Open window") and `claude://code/new?folder=<url-encoded path>` (a new session in that folder). Both are undocumented and found in the app bundle. Whether the `new` link takes a first prompt is not confirmed.
- Windows Terminal (`wt.exe`) is installed.

### Decisions made with the user

- **Reread**: a past session opens read-only in the town. It shows the user's prompts and Claude's replies (Markdown, as in the dialogue box) read from the transcript file. No Claude process starts, no tokens are spent.
- **Pick up a desktop session**: "Resume" opens it in the desktop app with the `continue` link. Same session, full memory.
- **Pick up a terminal session**: "Continue in new session" starts a new terminal session in the same folder whose first message is a handoff built by RepoManager from the transcript, with no Claude call. Never `claude --resume`.
- **New session**: "New session" on a repo, with a Terminal or Desktop choice each time.
- The non-goal "Starting new sessions from the town" is dropped.

### Claude's calls (the user accepted the picture)

- **Repo panel**: clicking a building (not a worker) opens a panel for that repo with the two "New session" buttons and the list of past sessions, newest first by last activity. Each row: title, date and time, a Desktop or Terminal tag. Worktree sessions are listed under the main repo with the worktree name. Archived desktop sessions are listed, marked archived. Sessions that are live in the town are not listed (their worker is the way in). Esc closes the panel; the panel follows the town's keyboard rules (no town keys while typing).
- **Which sessions belong to a repo**: a transcript belongs to the RepoManager project whose folder (or one of whose worktrees) contains the transcript's `cwd`, using the same mapping as live sessions (`ConfigService.InstanceForPath`). A session is "desktop" when a desktop index file names its id as `cliSessionId`, else "terminal". Transcripts of subagents and sessions with no user prompt are not listed.
- **Transcript view**: user prompts and assistant text only; tool calls are shown as one short line each ("Ran `npm test`", "Edited `App.tsx`") or left out. Long sessions load the last part first, with "Load earlier".
- **Handoff message**, built from the transcript, kept short (a few KB at most):
  - a first line saying this continues an earlier session, with its title and date;
  - the user's prompts in order, each shortened;
  - Claude's last reply in full (shortened if very long);
  - the transcript's full path, with a note to read it only if details are needed.
  It is shown in the panel before starting, so the user can edit it or add the next instruction.
- **Starting a terminal session**: `wt.exe -d <folder> claude <handoff or first prompt>`. The prompt is passed as one argument with no shell in between, so nothing in it is interpreted. If `wt.exe` is missing, a new `cmd` window in the folder runs `claude` the same way. The new session shows up in the town through its hooks like any other.
- **Starting a desktop session**: the `new?folder=` link. No first prompt for now; the user types it in the desktop app. If the builder confirms the link takes a prompt (for example `q=`), it may be used.
- **New session prompt (terminal)**: an optional first-prompt box under the Terminal button. Empty starts `claude` with no prompt.
- **Failures**: a link or process that fails to open says so in the panel and keeps any typed text. Index or transcript files that cannot be read are skipped, never shown as errors per row.
- **Safety**: the daemon only reads `.jsonl` files under `~/.claude/projects` and `.json` files under the desktop app's `claude-code-sessions` folder; the web UI asks by session id, never by path. Paths and prompts go to the started process as arguments, never through a shell string. Opening a session or starting one is a user click only, never automatic.

### Left to the builder (this section)

- Endpoint paths and DTO shapes (for example `GET /api/town/history?project=`, `GET /api/town/history/<id>`, `POST /api/town/start`).
- Caching the session list (transcripts can be large; read only what the list needs, such as the head for titles and the file time for dates).
- How many prompts the handoff includes and how much each is shortened.
- Panel layout and art, in the town's pixel style.

## Left to the builder

- Event names, DTO shapes, endpoint paths.
- Canvas engine details, sprite art, sounds (the prototype's square-wave beeps are a fine start).
- How the minimap and camera are implemented.
- Exact XP numbers and level thresholds.
- How "Open window" opens a desktop session: decided, see "Built so far".
- Stale session cleanup (the prototype drops sessions untouched for 8 hours).
- Splitting the work into phases. A sensible order: daemon events and hook, then town screen read-only, then dialogue and inbox sending, then permissions, then notifications, then game elements.
