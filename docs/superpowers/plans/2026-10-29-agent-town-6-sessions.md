# Agent Town, Plan 6: Past sessions and new sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From a repo's building the user can reread past sessions without spending tokens, resume a desktop session in the desktop app, continue a terminal session in a new terminal session with a handoff, and start a new session in a terminal or in the desktop app.

**Architecture:** A new `SessionHistory` in Core lists a project's past transcripts from Claude Code's `projects` folder, joins them with the desktop app's session index, reads one transcript as a page of entries, and builds the handoff text. A new `SessionStarter` in Core launches Windows Terminal or opens the desktop app's `new` link, and holds a first prompt until the new terminal session's `SessionStart` arrives, then sends it through the existing inbox. The daemon serves both under `/api/town/history*` and `/api/town/start*`. The web adds a `RepoPanel` opened by clicking a building, with a list, a read-only transcript view and the start buttons.

**Tech Stack:** .NET 10, xUnit; React 19, TypeScript, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-27-agent-town-design.md` ("Past sessions and new sessions").

**Series:** Plan 6. Plans 1 to 5 are merged.

## Global Constraints

- Viewing a past session never starts Claude and never calls a model. Only files are read.
- Never run `claude --resume` or `claude --continue`.
- The daemon reads only `<id>.jsonl` files directly inside a folder of `<claude config dir>\projects` (claude config dir = `CLAUDE_CONFIG_DIR`, else `%USERPROFILE%\.claude`) and only `local_*.json` files under the desktop index folder (`REPOMANAGER_CLAUDE_DESKTOP_DIR`, else `%APPDATA%\Claude\claude-code-sessions`). Files in subfolders (subagent transcripts) are skipped.
- The web asks by session id, never by path. A session id must match `^[0-9a-fA-F-]{36}$` or the request fails.
- Folders to start in come from the daemon (the project instance root, or a past transcript's `cwd` that still exists and still maps to the same project), never from the request.
- Processes start with `UseShellExecute = false` and `ArgumentList`, never a command string through a shell. Links start with `WindowActivator.OpenLink`.
- Terminal start: `wt.exe -d <folder> claude`; if `wt.exe` cannot start, `cmd.exe /k claude` with `WorkingDirectory = <folder>`. No prompt on the command line.
- Desktop start: `claude://code/new?folder=<Uri.EscapeDataString(folder)>`. No prompt. Desktop resume: existing `api.townOpenHost(hostSessionId)`.
- Held first prompt: sent with `AgentInbox.SendAsync(..., AgentInbox.FromTownUser(text))` to the first `SessionStart` with `Surface == "terminal"` whose normalized `Cwd` equals the start folder, within 2 minutes; else status `expired`.
- Handoff: at most 4000 characters. First line `This continues an earlier session: "<title>" (<yyyy-MM-dd HH:mm>).`; then `What I asked:` with each prompt on a `- ` line shortened to 200 characters (the most recent 15 when there are more, with `- (… N earlier)` first); then `Your last reply:` and the last assistant text shortened to 1500 characters; last line `Full transcript (read it only if you need details): <path>`.
- Transcript page size: 150 entries. Tool calls become one `tool` entry each: `Ran <first 80 chars of command>` for Bash, `Edited <file name>` for Edit/Write, else `Used <tool name>`.
- Listed sessions: newest first by last activity; excluding live town sessions, subagent (`isSidechain`) only files, and sessions with no user prompt.
- All file reads tolerate missing, locked, half-written and foreign-format files: skip, never throw.

## Review Focus

1. **A transcript of many MB.** Expected: the list stays fast (list reads only the first and last 64 KB of each file and caches by path, size and write time); the view pages from the end. Test in Task 1.
2. **A past session whose folder was deleted or moved out of the project.** Expected: still listed and readable; "Continue in new session" starts in the project root instead. Test in Task 3.
3. **Two terminal starts in the same folder within 2 minutes.** Expected: each held prompt goes to one session, in start order, never both to one. Test in Task 2.
4. **A desktop index file for a session whose transcript is gone, or a transcript with no index entry.** Expected: index-only entries are not listed; a transcript without an index entry is "terminal" when its `entrypoint` is `cli`, else "desktop" with no resume button (shows "Continue in new session"). Test in Task 1.
5. **Clicking a building while typing in the dialogue box, or with the dialogue open.** Expected: clicking a building closes the dialogue and opens the panel; town keys stay off while typing in the panel. Test in Task 4.

---

### Task 1: SessionHistory in Core

**Files:**
- Create: `src/RepoManager.Core/Agents/SessionHistory.cs`
- Modify: `src/RepoManager.Contracts/AgentDtos.cs` (new DTOs)
- Test: `tests/RepoManager.Tests/SessionHistoryTests.cs`

**Interfaces:**
- Produces:
  - `record PastSessionDto { string Id; string? Project; string? Worktree; string Title; string Surface /* desktop|terminal */; string? HostSessionId; bool Archived; DateTimeOffset Started; DateTimeOffset LastActivity; string Cwd; }`
  - `record TranscriptEntryDto { string Role /* user|assistant|tool */; string Text; DateTimeOffset? At; }`
  - `record TranscriptPageDto { string Id; IReadOnlyList<TranscriptEntryDto> Entries; int Start; int Total; }`
  - `sealed class SessionHistory(string projectsDir, string desktopDir, Func<string, (string Project, string? Worktree)?> resolve)` with
    - `IReadOnlyList<PastSessionDto> List(string project, IReadOnlySet<string> liveIds)`
    - `PastSessionDto? Find(string id)`
    - `TranscriptPageDto? Page(string id, int? before)` — entries `[max(0, end-150), end)` where `end = before ?? Total`
    - `string? Handoff(string id)`
    - `string? TranscriptPath(string id)`
    - `static bool ValidId(string? id)`

- [ ] **Step 1: Write failing tests** with a temp `projects` and `desktop` folder built per test from small JSONL lines (`{"type":"user","message":{"content":"fix login"},"cwd":"<proj>","entrypoint":"cli","timestamp":"..."}`, assistant text blocks, a `tool_use` Bash block, `ai-title` / `custom-title` lines):
  - `List_ReturnsProjectSessionsNewestFirst_WithTitles` (custom-title beats agent-name beats ai-title beats first prompt shortened to 48 chars)
  - `List_MarksDesktopFromIndex_WithHostIdAndArchived`
  - `List_SkipsLiveIds_SubagentFolders_NoPromptFiles_AndOtherProjects`
  - `List_TranscriptWithoutIndex_IsTerminalForCli_ElseDesktopWithoutHostId`
  - `List_IndexWithoutTranscript_IsNotListed`
  - `List_LargeFile_ReadsHeadAndTailOnly` (10 MB file of padding lines between first prompt and a final `custom-title`; title found; second call served from cache, asserted by a read counter)
  - `Page_ReturnsLast150_AndEarlierWithBefore` (400 entries: `Start == 250`, `Total == 400`; `before: 250` gives `Start == 100`)
  - `Page_ToolEntries_AreShortLines` (`Ran npm test`, `Edited App.tsx`, `Used Grep`)
  - `Handoff_FollowsFormat_AndCapsAt4000` (exact first and last lines from Global Constraints; 30 prompts give `- (… 15 earlier)`)
  - `InvalidId_OrPathLikeId_ReturnsNull` (`..\\x`, `a/b`, `""`)
  - `CorruptLines_AreSkipped`
- [ ] **Step 2: Run** `dotnet test --filter SessionHistoryTests` — expected FAIL (type missing).
- [ ] **Step 3: Implement.** Reuse `TranscriptReader`'s line rules (`IsPrompt`, sidechain skip, title line types); move the shared helpers into `internal static` members of `TranscriptReader` rather than copying. Desktop index: enumerate `local_*.json` recursively under `desktopDir`, read `sessionId`, `cliSessionId`, `title`, `isArchived`, `lastActivityAt`; desktop `title` wins over transcript titles. Cache `List` file scans in a `ConcurrentDictionary<string, (long Size, DateTime Mtime, Summary)>`.
- [ ] **Step 4: Run** `dotnet test --filter SessionHistoryTests` — expected PASS.
- [ ] **Step 5: Commit** `feat(town): read past sessions and build a handoff from a transcript`

### Task 2: SessionStarter in Core

**Files:**
- Create: `src/RepoManager.Core/Agents/SessionStarter.cs`
- Test: `tests/RepoManager.Tests/SessionStarterTests.cs`

**Interfaces:**
- Produces:
  - `record StartStatusDto { string Id; string State /* pending|sent|expired|failed|none */; string? Message; }`
  - `sealed class SessionStarter(Func<ProcessStartInfo, bool> start, Func<string, bool> openLink, Func<DateTimeOffset> clock, Func<string, string, string, Task<(bool Ok, string Message)>> send /* socket, token, text */)` with
    - `StartStatusDto StartTerminal(string folder, string? text)` — `State = text is null ? "none" : "pending"`, or `failed` when both `wt.exe` and `cmd.exe` fail
    - `ActionResultDto StartDesktop(string folder)`
    - `Task OnSessionStart(AgentSessionDto s, (string Socket, string Token)? inbox)` — consumes the oldest unexpired pending start for that folder
    - `StartStatusDto Status(string id)`
  - `static string DesktopNewLink(string folder)`
- [ ] **Step 1: Write failing tests** with fake `start`, `openLink`, `send` and a settable clock:
  - `Terminal_StartsWtWithFolderAndClaude_ArgumentList` (`FileName == "wt.exe"`, args `["-d", folder, "claude"]`, `UseShellExecute == false`)
  - `Terminal_FallsBackToCmd_WhenWtFails` (`cmd.exe`, args `["/k", "claude"]`, `WorkingDirectory == folder`)
  - `HeldPrompt_SentOnMatchingTerminalSessionStart` (state `sent`; `send` got `AgentInbox.FromTownUser(text)`)
  - `HeldPrompt_IgnoresDesktopSessions_AndOtherFolders` (folder compare normalized: trailing slash and case)
  - `HeldPrompt_ExpiresAfter2Minutes`
  - `TwoStartsSameFolder_GoToTwoSessionsInOrder`
  - `SendFailure_IsFailedWithMessage`
  - `DesktopNewLink_EscapesFolder` (`C:\a b` gives `claude://code/new?folder=C%3A%5Ca%20b`)
- [ ] **Step 2: Run** `dotnet test --filter SessionStarterTests` — expected FAIL.
- [ ] **Step 3: Implement.** Pending starts in a locked `List`, pruned on every call. Statuses kept 10 minutes.
- [ ] **Step 4: Run** `dotnet test --filter SessionStarterTests` — expected PASS.
- [ ] **Step 5: Commit** `feat(town): start terminal and desktop sessions, hand the first prompt over through the inbox`

### Task 3: Daemon endpoints

**Files:**
- Modify: `src/RepoManager.App/Daemon/DaemonHost.cs` (create `History`, `Starter`; call `Starter.OnSessionStart` from `Agents.Changed` when the session's state is `idle` and its message is `Session started`)
- Modify: `src/RepoManager.App/Daemon/Api.cs` (endpoints below)
- Modify: `src/RepoManager.Contracts/AgentDtos.cs` (`TownStartRequest { string Surface; string Project; string? Worktree; string? FromSessionId; string? Text; }`)
- Test: `tests/RepoManager.Tests/TownApiTests.cs`, `tests/RepoManager.Tests/DaemonTests.cs` (fixture sets `REPOMANAGER_CLAUDE_DESKTOP_DIR` to `Home\desktop`; projects dir comes from the existing `CLAUDE_CONFIG_DIR`)

**Interfaces:**
- Consumes: Task 1 and Task 2 types.
- Produces endpoints:
  - `GET /api/town/history?project=<name>` → `PastSessionDto[]` (live ids from `d.Agents.Snapshot()`)
  - `GET /api/town/history/{id}?before=<n>` → `TranscriptPageDto`, 404 for an unknown or invalid id
  - `GET /api/town/history/{id}/handoff` → `{ "text": string }`, 404 likewise
  - `POST /api/town/start` (`TownStartRequest`) → `StartStatusDto`. Folder: `FromSessionId`'s `Cwd` when it exists and `Config.InstanceForPath(cwd)` gives the same project, else the instance root from `Config.FindInstance(Ids.Instance(Project, Worktree))`; unknown project → `failed` "unknown project". `Surface == "desktop"` ignores `Text`.
  - `GET /api/town/start/{id}` → `StartStatusDto`
- [ ] **Step 1: Write failing API tests:**
  - `History_ListsFixtureTranscript_ForDemoProject`
  - `History_Page_UnknownOrInvalidId_Is404`
  - `Start_UnknownProject_Fails`
  - `Start_FromSessionWithDeletedFolder_UsesProjectRoot` (assert through a test seam: `DaemonHost.Starter` built with a recording `start` func when `REPOMANAGER_TEST_NO_LAUNCH=1`, which the fixture sets, so tests never open terminals)
  - `Start_HeldPrompt_SentWhenTerminalSessionStarts` (post a `SessionStart` hook with socket and token, entrypoint `cli`, cwd = project; status becomes `sent` or `failed` with the inbox error, never stays `pending`)
- [ ] **Step 2: Run** `dotnet test --filter TownApiTests` — expected FAIL.
- [ ] **Step 3: Implement** the endpoints and wiring.
- [ ] **Step 4: Run** `dotnet test` — expected PASS for the whole suite.
- [ ] **Step 5: Commit** `feat(town): history and start endpoints`

### Task 4: Repo panel in the web

**Files:**
- Modify: `web/src/api/types.ts`, `web/src/api/client.ts` (`townHistory(project)`, `townTranscript(id, before?)`, `townHandoff(id)`, `townStart(req)`, `townStartStatus(id)`)
- Modify: `web/src/lib/town/render.ts` (`lotAt(lots, x, y, levelOf): number | null`, the index of the building under a scene point, using `lotX` and `buildingHeight`)
- Create: `web/src/views/town/RepoPanel.tsx`, `web/src/views/town/RepoPanel.test.tsx`
- Modify: `web/src/views/TownView.tsx` (in `onPointerUp`, when no worker is hit and `lotAt` hits a lot that is not temporary, close the dialogue and open `RepoPanel` for it; Esc closes the panel first; `isTypingTarget` already keeps town keys off in its inputs)
- Modify: `web/src/styles.css` (panel in the dialogue box's pixel frame style, right side, full height)
- Test: `web/src/lib/town/render.test.ts`, `web/src/views/TownView.test.tsx`

**Interfaces:**
- Consumes: Task 3 endpoints. Markdown rendering from `web/src/views/town/Markdown.tsx`.
- Produces: `RepoPanel({ project, worktrees, onClose })`.

Panel contents, top to bottom:
1. Repo name, close button.
2. "New session": buttons **Terminal** and **Desktop**; under Terminal an optional first-prompt textarea (Enter starts, Shift+Enter new line).
3. Past sessions list: title, `yyyy-MM-dd HH:mm`, tag `Desktop` or `Terminal`, worktree name, `archived` when set. Empty: "No past sessions."
4. Selecting a row opens the transcript view in the panel: entries (user right-aligned label "You", assistant via `Markdown`, tool lines dimmed), "Load earlier" at the top while `Start > 0`, and at the bottom:
   - desktop with `hostSessionId`: **Resume in desktop** → `api.townOpenHost`
   - otherwise: **Continue in new session** → loads `townHandoff` into an editable textarea, then **Start in terminal** → `townStart({ surface: 'terminal', project, worktree, fromSessionId, text })`
5. After a terminal start with text: "Waiting for the session…", polling `townStartStatus` every 2 s until `sent` ("Sent"), `expired` or `failed` ("Not sent: <message>", textarea kept). Any failed call shows its message and keeps typed text.

- [ ] **Step 1: Write failing tests:**
  - `render.test.ts`: `lotAt_HitsBuildingBody_MissesGapsAndSky`
  - `RepoPanel.test.tsx` (mocked `api`): `ListsSessions_WithTags`; `Row_OpensTranscript_LoadEarlierFetchesBefore`; `DesktopRow_ShowsResume_CallsOpenHost`; `TerminalRow_Continue_LoadsHandoff_StartsWithEditedText`; `NewTerminal_EnterStarts_ShiftEnterNewLine`; `NewDesktop_StartsWithoutText`; `StartPending_PollsUntilSent`; `StartExpired_KeepsText`
  - `TownView.test.tsx`: `ClickBuilding_OpensPanel_ClosesDialogue`; `ClickWorker_StillOpensDialogue`; `Esc_ClosesPanel`; `ArrowKeys_DoNotMoveBoss_WhileTypingInPanel`
- [ ] **Step 2: Run** `cd web && npx vitest run src/views/town/RepoPanel.test.tsx src/views/TownView.test.tsx src/lib/town/render.test.ts` — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `cd web && npx vitest run && npx tsc --noEmit` — expected PASS, no type errors.
- [ ] **Step 5: Commit** `feat(town): repo panel with past sessions, transcripts and new sessions`

### Task 5: Check in the real app

- [ ] **Step 1:** Rebuild and reinstall with `install.ps1` (how the user updates RepoManager). Open the town.
- [ ] **Step 2:** Click the `repo-manager` building. Expected: panel lists this repo's past sessions with Desktop/Terminal tags; opening one shows its prompts and replies; no `claude` process starts (check Task Manager).
- [ ] **Step 3:** Resume a desktop session. Expected: the desktop app opens it.
- [ ] **Step 4:** Continue a terminal session. Expected: Windows Terminal opens in the folder, the session appears in the town, the handoff arrives as its first message, panel shows "Sent".
- [ ] **Step 5:** New Desktop session. Expected: the desktop app opens a new session in the repo folder. If the link fails on this app version, record it in the spec's "Built so far" and leave the button showing the error.
- [ ] **Step 6:** Update the spec's status line to "plans 1 to 6 built" and add any decisions made while building to "Built so far". Commit `docs: Agent Town spec after plan 6`.
