# Agent Town, Plan 1: Backend (sessions, inbox, permissions, hooks) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The RepoManager daemon knows every Claude Code session on the machine, streams their state over SSE, can send a typed message into a session's inbox, and can answer permission prompts while the town is focused.

**Architecture:** A thin `devm town-hook` command forwards each Claude Code hook's stdin JSON plus a few environment variables to the daemon. The daemon parses it (`Core/Agents`), keeps sessions in memory in an `AgentRegistry`, publishes `agent` / `agentEnd` events through the existing `EventHub`, sends replies through the session's named-pipe inbox (`AgentInbox`), and holds `PermissionRequest` calls in a `PermissionBroker` only while the town reports focus. `devm claude-setup` installs the hooks.

**Tech Stack:** .NET 10 (ASP.NET Core minimal APIs, `System.IO.Pipes`), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-27-agent-town-design.md`

**Series:** This is plan 1 of 5. Each later plan is written after the previous one lands, against the real code:
2. Town screen, read-only (`town` UiMode, canvas street sorted by urgency, minimap, camera, labels, tab title).
3. Dialogue box and sending (RPG dialogue, inbox replies, permission menu, quest log, "Open window").
4. Attention (shell focus reporting, tray blink, Windows notifications, sounds, settings).
5. Game elements (boss character, XP and levels, building growth).

## Global Constraints

- Nothing may block or delay real work. Status hooks always exit 0, print nothing, and give up after 1 s. Only `PermissionRequest` may wait, and only while the town is focused.
- A `PermissionRequest` that arrives while the town is not focused gets "no decision" immediately (no grace wait).
- Losing focus releases every waiting `PermissionRequest` at once with "no decision".
- The messaging token is held in daemon memory only: never in a DTO, SSE event, log line, file or exception message.
- The town hook never starts the daemon (`ConnectAsync(start: false)`).
- Inbox wire format, exactly: line 1 `{"type":"auth","token":"<token>"}`, line 2 `{"type":"user","message":{"role":"user","content":"<text>"}}`, each followed by `\n`, then close.
- Session states: `idle`, `working`, `waiting`, `done`, `failed`. `done` stays until the next `UserPromptSubmit`.
- Sessions untouched for 8 hours are dropped.
- Prerequisite: the uncommitted Mini view work is committed (or set aside by the user) before Task 1.

## Review Focus

1. **Daemon restarts while sessions run.** The registry is empty after restart; the next hook event from a session must recreate it with its inbox (env vars are sent on every event). Test in Task 2.
2. **Hook stdin with a BOM or a non-UTF-8 code page.** Same handling as `Hook.cs` (skip to first `{`). Test in Task 6.
3. **Session folder not in RepoManager.** `Project` is null and `Folder` is the folder name; the session is still listed. Test in Task 2.
4. **Inbox pipe gone (session closed without SessionEnd).** `SendAsync` returns a failure within 2 s and the API returns `Ok=false` with a readable message, never a 500. Test in Task 3.
5. **Two permission prompts at once from one session (parallel tool calls).** Each gets its own `RequestId`; answering one does not release the other. Test in Task 4.

---

## File Structure

- Create `src/RepoManager.Contracts/AgentDtos.cs`: `AgentSessionDto`, `PermissionPromptDto`, `TownHookEnvelope`, `PermissionDecisionDto`, `TownSendRequest`, `PermissionAnswerRequest`, `TownFocusRequest`.
- Create `src/RepoManager.Core/Agents/HookEventParser.cs`: hook JSON + env to a parsed `AgentHookEvent`.
- Create `src/RepoManager.Core/Agents/AgentRegistry.cs`: in-memory sessions, state rules, inbox secrets, sweep.
- Create `src/RepoManager.Core/Agents/AgentInbox.cs`: named-pipe sender.
- Create `src/RepoManager.Core/Agents/PermissionBroker.cs`: focus-gated waits.
- Modify `src/RepoManager.App/Daemon/EventHub.cs`: publish `agent` and `agentEnd`.
- Modify `src/RepoManager.App/Daemon/DaemonHost.cs`: own the registry, broker, sweep timer.
- Modify `src/RepoManager.App/Daemon/Api.cs`: `/api/town/*` endpoints.
- Create `src/RepoManager.Cli/TownHook.cs`; modify `Cli.cs` (route `town-hook`).
- Modify `src/RepoManager.Cli/ClaudeIntegration.cs`: install/remove town hooks; `--no-town`.
- Tests: `tests/RepoManager.Tests/AgentParserTests.cs`, `AgentRegistryTests.cs`, `AgentInboxTests.cs`, `PermissionBrokerTests.cs`, `TownApiTests.cs`, additions to `ClaudeSetupTests.cs`.

---

### Task 1: Hook event parser and DTOs

**Files:**
- Create: `src/RepoManager.Contracts/AgentDtos.cs`
- Create: `src/RepoManager.Core/Agents/HookEventParser.cs`
- Test: `tests/RepoManager.Tests/AgentParserTests.cs`

**Interfaces:**
- Produces (Contracts):
  - `sealed record TownHookEnvelope { required string Json; string? Socket; string? Token; string? Entrypoint; string? HostSessionId; }` (what the hook posts).
  - `sealed record AgentSessionDto { required string Id; string? Project; string? Worktree; required string Folder; required string Cwd; required string State; string Message = ""; string? Prompt; string? LastReply; required string Surface; string? HostSessionId; DateTimeOffset Since; DateTimeOffset Touched; bool CanSend; IReadOnlyList<PermissionPromptDto> Prompts = []; }`
  - `sealed record PermissionPromptDto { required string RequestId; required string ToolName; required string Summary; bool Answerable; }`
  - `sealed record PermissionDecisionDto(string? Behavior, bool Always);` (`Behavior` is `"allow"`, `"deny"` or null for no decision).
  - `sealed record TownSendRequest(string SessionId, string Text);`, `sealed record PermissionAnswerRequest(string RequestId, string Behavior, bool Always);`, `sealed record TownFocusRequest(bool Focused);`
- Produces (Core): `sealed record AgentHookEvent(string Kind, string SessionId, string Cwd, string? Prompt, string? ToolName, string? ToolInputJson, string? Message, string? NotificationType, string? LastReply, string? PermissionSuggestionsJson, string? Socket, string? Token, string Surface, string? HostSessionId);` and `static AgentHookEvent? HookEventParser.Parse(TownHookEnvelope env)`.

- [ ] **Step 1: Write failing tests** in `AgentParserTests`:
  - `Parse_UserPromptSubmit`: JSON `{"hook_event_name":"UserPromptSubmit","session_id":"abc","cwd":"C:\\r\\demo","prompt":"fix the bug"}` gives `Kind=="UserPromptSubmit"`, `SessionId=="abc"`, `Prompt=="fix the bug"`.
  - `Parse_Stop_TakesLastAssistantMessage`: `last_assistant_message` lands in `LastReply`.
  - `Parse_PermissionRequest`: `tool_name`, raw `tool_input` and raw `permission_suggestions` are kept.
  - `Parse_SurfaceFromEntrypoint`: `"claude-desktop"` and `"claude-desktop-3p"` give `"desktop"`, `"cli"` gives `"terminal"`, null or anything else gives `"other"`.
  - `Parse_RejectsBadSessionId`: a `session_id` with characters outside `[A-Za-z0-9_.-]`, longer than 80, or missing gives null.
  - `Parse_UnknownEvent_ReturnsNull`: `"hook_event_name":"PreCompact"` gives null.
- [ ] **Step 2: Run** `dotnet test --filter AgentParserTests`. Expected: FAIL (types do not exist).
- [ ] **Step 3: Implement** the DTOs and `HookEventParser.Parse`. Accepted kinds: `SessionStart`, `UserPromptSubmit`, `PostToolUse`, `Notification`, `PermissionRequest`, `Stop`, `SessionEnd`. Use `JsonDocument`; a parse error returns null.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): parse Claude Code hook events`.

### Task 2: Agent registry

**Files:**
- Create: `src/RepoManager.Core/Agents/AgentRegistry.cs`
- Test: `tests/RepoManager.Tests/AgentRegistryTests.cs`

**Interfaces:**
- Consumes: `AgentHookEvent`, `AgentSessionDto`, `PermissionPromptDto` (Task 1).
- Produces:
  - `AgentRegistry(Func<string, (string Project, string? Worktree)?> resolve, Func<DateTimeOffset> clock)`.
  - `AgentSessionDto? Apply(AgentHookEvent e)` returns the updated session, or null when the event ended it.
  - `IReadOnlyList<AgentSessionDto> Snapshot()`.
  - `(string Socket, string Token)? InboxFor(string sessionId)`.
  - `void SetPrompts(string sessionId, IReadOnlyList<PermissionPromptDto> prompts)` (used by Task 4 wiring).
  - `int Sweep(TimeSpan maxAge)` returns the number of sessions removed.
  - `event Action<AgentSessionDto>? Changed; event Action<string>? Ended;`

- [ ] **Step 1: Write failing tests** with a fake clock and a resolver that maps `C:\r\demo` to `("demo", null)`:
  - `SessionStart_CreatesIdle`: state `idle`, message `"Session started"`, `Project=="demo"`, `Folder=="demo"`.
  - `UserPromptSubmit_Working_WithPrompt`: state `working`, `Prompt` set, `Message` is the prompt clipped to 140 chars.
  - `PostToolUse_Working_MessageUsedTool`: message `"Used Bash"`.
  - `Notification_PermissionOrElicitation_Waiting`: types `permission_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, `agent_needs_input` set `waiting`; `idle_prompt` leaves the state unchanged.
  - `Stop_Done_KeepsUntilNextPrompt`: `Stop` sets `done` with `LastReply`; a later `PostToolUse` does not change `done`; a `UserPromptSubmit` sets `working`.
  - `Since_ChangesOnlyOnStateChange`: two `PostToolUse` events keep the first `Since`.
  - `SessionEnd_RemovesAndRaisesEnded`.
  - `UnknownFolder_StillListed` (Review Focus 3): cwd `D:\elsewhere\tool` gives `Project==null`, `Folder=="tool"`.
  - `FirstEventAfterRestart_RecreatesWithInbox` (Review Focus 1): a `PostToolUse` for an unknown id with socket and token creates the session (`working`) and `InboxFor` returns them; `CanSend` is true.
  - `Token_NeverInDto`: serialize `Snapshot()` with `Protocol.Json`; the token string does not occur.
  - `Sweep_DropsAfterMaxAge`: advance clock 8h + 1s; `Sweep(TimeSpan.FromHours(8))` returns 1 and raises `Ended`.
- [ ] **Step 2: Run** `dotnet test --filter AgentRegistryTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `AgentRegistry`. Thread-safe with a lock. Inbox secrets in a private `Dictionary<string,(string,string)>`, updated whenever an event carries both. `CanSend` = inbox known. `PermissionRequest` events do not change state here (Task 4 owns them). Raise events outside the lock.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): in-memory registry of Claude sessions`.

### Task 3: Inbox sender

**Files:**
- Create: `src/RepoManager.Core/Agents/AgentInbox.cs`
- Test: `tests/RepoManager.Tests/AgentInboxTests.cs`

**Interfaces:**
- Produces: `static Task<(bool Ok, string Message)> AgentInbox.SendAsync(string pipePath, string token, string text, CancellationToken ct)`.

- [ ] **Step 1: Write failing tests:**
  - `Send_WritesAuthThenUserLine`: start a `NamedPipeServerStream` named `rm-test-<guid>`; call `SendAsync(@"\\.\pipe\rm-test-<guid>", "tok", "hello \"x\"\nline2", ct)`; read all text on the server. Expected exactly: `{"type":"auth","token":"tok"}\n{"type":"user","message":{"role":"user","content":"hello \"x\"\nline2"}}\n` (newline inside content is JSON-escaped). Result `Ok==true`.
  - `Send_MissingPipe_FailsFast` (Review Focus 4): unknown pipe returns `Ok==false` within 2 s, message contains `"inbox"`.
  - `Send_EmptyText_Rejected`: whitespace-only text returns `Ok==false` without connecting.
- [ ] **Step 2: Run** `dotnet test --filter AgentInboxTests`. Expected: FAIL.
- [ ] **Step 3: Implement** with `NamedPipeClientStream(".", name, PipeDirection.Out)` (strip the `\\.\pipe\` prefix), `ConnectAsync` with a 2 s timeout, `Utf8JsonWriter` or `JsonSerializer` for the two lines, flush, dispose. Catch exceptions into `(false, "could not reach the session inbox: <reason>")`; never include the token in the message.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): send messages into a session inbox`.

### Task 4: Permission broker

**Files:**
- Create: `src/RepoManager.Core/Agents/PermissionBroker.cs`
- Test: `tests/RepoManager.Tests/PermissionBrokerTests.cs`

**Interfaces:**
- Consumes: `PermissionDecisionDto`, `PermissionPromptDto` (Task 1).
- Produces:
  - `PermissionBroker()`; `bool Focused { get; }`; `void SetFocused(bool focused)`.
  - `Task<PermissionDecisionDto> WaitAsync(string sessionId, PermissionPromptDto prompt, TimeSpan max, CancellationToken ct)`.
  - `bool Answer(string requestId, PermissionDecisionDto decision)`.
  - `IReadOnlyList<PermissionPromptDto> PromptsFor(string sessionId)`.
  - `void ClearSession(string sessionId)` removes the session's non-answerable prompts.
  - `event Action<string>? PromptsChanged;` (argument is the session id).

- [ ] **Step 1: Write failing tests:**
  - `Unfocused_ReturnsNoDecisionImmediately`: `Behavior==null`, the task is already completed; `PromptsFor` lists the prompt with `Answerable==false`; after `ClearSession` it is gone.
  - `Focused_WaitsForAnswer`: `SetFocused(true)`; `WaitAsync` pending; `Answer(id, new("allow", false))` completes it with `allow`.
  - `FocusLost_ReleasesAll`: two pending waits from two sessions; `SetFocused(false)` completes both with `Behavior==null`.
  - `TwoPromptsSameSession_Independent` (Review Focus 5): answer one; the other stays pending.
  - `Timeout_ReturnsNoDecision`: `max` of 100 ms gives `Behavior==null`.
  - `Cancelled_ReturnsNoDecision`: cancelling `ct` (hook process gone) gives `Behavior==null` and removes the prompt.
  - `Answer_UnknownId_ReturnsFalse`.
- [ ] **Step 2: Run** `dotnet test --filter PermissionBrokerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Pending waits are `TaskCompletionSource<PermissionDecisionDto>` keyed by `RequestId`. A released or answered wait removes its prompt from the answerable list. An unfocused arrival is recorded as a non-answerable prompt (`Answerable=false`) so the town can show "Answer in the window"; it is cleared by `void ClearSession(string sessionId)`, which the daemon calls on the session's next non-permission event. Raise `PromptsChanged` on every add or remove.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): focus-gated permission broker`.

### Task 5: Daemon wiring and API

**Files:**
- Modify: `src/RepoManager.App/Daemon/DaemonHost.cs` (construct `Agents`, `Permissions`; 60 s sweep timer, dispose it)
- Modify: `src/RepoManager.App/Daemon/EventHub.cs` (constructor takes `AgentRegistry`; publish `agent` with `AgentSessionDto`, `agentEnd` with `{ id }`; update the `EventDto` doc comment in `Dtos.cs`)
- Modify: `src/RepoManager.App/Daemon/Api.cs`
- Test: `tests/RepoManager.Tests/TownApiTests.cs` (uses `DaemonFixture`)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces (HTTP, all under the existing token check):
  - `POST /api/town/hook` body `TownHookEnvelope` returns `ActionResultDto`. Parses, applies to the registry, calls `Permissions.ClearSession` for non-permission events.
  - `POST /api/town/permission` body `TownHookEnvelope` returns `PermissionDecisionDto`. Parses, ensures the session exists, builds a `PermissionPromptDto` (summary = the `command` field of `tool_input` for Bash/PowerShell, else the tool name), waits with `max` 9 minutes and `ctx.RequestAborted`.
  - `POST /api/town/permission/answer` body `PermissionAnswerRequest` returns `ActionResultDto`.
  - `POST /api/town/send` body `TownSendRequest` returns `ActionResultDto` (fail when unknown session or no inbox).
  - `POST /api/town/focus` body `TownFocusRequest` returns `ActionResultDto`.
  - `GET /api/town/sessions` returns `IReadOnlyList<AgentSessionDto>` with `Prompts` filled from the broker.
- `DaemonHost` resolver: `cwd => Config.InstanceForPath(cwd) is { } p ? (p.Project, p.Worktree) : null`.
- Broker `PromptsChanged` and registry `Changed` both publish the session's current DTO (with prompts) as `agent`.

- [ ] **Step 1: Write failing tests:**
  - `Hook_SessionStart_ListedAndStreamed`: post a SessionStart envelope with cwd = fixture `Project`; `GET /api/town/sessions` has it with `Project=="demo"`; an SSE subscriber started before the post receives a `data:` line with `"type":"agent"`.
  - `Hook_BadJson_Returns400OrFail`: returns `Ok==false`, no session.
  - `Permission_Unfocused_NoDecision`: returns `{"behavior":null}` quickly; the session lists one prompt with `answerable:false`.
  - `Permission_Focused_AnsweredFromTown`: focus true; start the permission POST; poll sessions until a prompt appears; answer `allow`; the POST returns `behavior:"allow"`.
  - `Permission_FocusLost_Releases`: focus true; start the POST; focus false; POST returns `behavior:null`.
  - `Send_UnknownSession_Fails` and `Send_ToFakeInbox_Succeeds` (fake pipe server as in Task 3, envelope carries its socket and token).
  - `Sessions_NeverContainToken`: raw response text of `GET /api/town/sessions` does not contain the token.
- [ ] **Step 2: Run** `dotnet test --filter TownApiTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the wiring and endpoints, following the existing `api.MapPost` style.
- [ ] **Step 4: Run** `dotnet test`. Expected: all tests pass, including existing `DaemonTests`.
- [ ] **Step 5: Commit** `feat(town): daemon API and SSE for Claude sessions`.

### Task 6: `devm town-hook`

**Files:**
- Create: `src/RepoManager.Cli/TownHook.cs`
- Modify: `src/RepoManager.Cli/Cli.cs:48` (route `town-hook`; add a usage line)
- Test: `tests/RepoManager.Tests/TownApiTests.cs` (runs `CliTests.DevmExe` against the fixture the way `DaemonTests.cs:277` runs `devm hook`; set `REPOMANAGER_HOME` to the fixture home)

**Interfaces:**
- Consumes: `TownHookEnvelope`, `PermissionDecisionDto`, `/api/town/hook`, `/api/town/permission`, `DaemonClient`.
- Produces: `static Task<int> TownHook.RunAsync()`.

- [ ] **Step 0: Confirm the `PermissionRequest` output format** in the Claude Code hooks reference (https://code.claude.com/docs/en/hooks.md). Expected shape: `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`, deny with `"behavior":"deny","message":"..."`, and "always" via `"updatedPermissions"` built from the input's `permission_suggestions`. Write what the docs say into a comment at the top of `TownHook.cs`; if it differs, follow the docs. In the same reference, look for a hook that reports a failed turn (for example a stop-failure event). If one exists, add it to the parser's accepted kinds, map it to state `failed` in the registry with a test like `Stop_Done_KeepsUntilNextPrompt`, and add it to `TownEvents` in Task 7. If none exists, `failed` stays unused and the spec's note covers it.
- [ ] **Step 1: Write failing tests:**
  - `TownHook_StatusEvent_PostsAndExitsZero`: pipe a Stop JSON (with UTF-8 BOM, Review Focus 2) into `devm town-hook` with `CLAUDE_CODE_ENTRYPOINT=cli`; exit 0, empty stdout; the session shows `done`, surface `terminal`.
  - `TownHook_NoDaemon_ExitsZeroFast`: point `REPOMANAGER_HOME` at an empty dir; exit 0 within 1.5 s, no daemon started.
  - `TownHook_Permission_PrintsDecision`: focus true; run the hook with a PermissionRequest JSON; answer `allow` via the API; stdout parses to `hookSpecificOutput.decision.behavior=="allow"`.
  - `TownHook_Permission_NoDecision_PrintsNothing`: unfocused; stdout empty, exit 0.
- [ ] **Step 2: Run** `dotnet test --filter TownHook`. Expected: FAIL.
- [ ] **Step 3: Implement.** Read stdin like `Hook.cs` (raw UTF-8, skip to first `{`). Build `TownHookEnvelope` with `CLAUDE_CODE_MESSAGING_SOCKET`, `CLAUDE_CODE_MESSAGING_TOKEN`, `CLAUDE_CODE_ENTRYPOINT`, `CLAUDE_CODE_HOST_SESSION_ID`. Peek `hook_event_name`: for `PermissionRequest` post to `api/town/permission` with a 590 s token and print the decision only when `Behavior` is not null; otherwise post to `api/town/hook` with a 1 s overall token. Every path returns 0; errors print only with `REPOMANAGER_HOOK_DEBUG=1`, never the token.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(town): devm town-hook forwards Claude hook events`.

### Task 7: Install the town hooks

**Files:**
- Modify: `src/RepoManager.Cli/ClaudeIntegration.cs`
- Modify: `src/RepoManager.Cli/Cli.cs:50` (pass `!a.Flag("no-town")`; usage line `devm claude-setup [--no-mcp] [--no-town]`)
- Test: `tests/RepoManager.Tests/ClaudeSetupTests.cs`

**Interfaces:**
- Produces: `public static readonly string[] TownEvents = ["SessionStart","UserPromptSubmit","PostToolUse","Notification","Stop","SessionEnd"];`, `public static bool AddTownHooks(JsonObject root, string command)`, `public static bool RemoveTownHooks(JsonObject root)`, `Setup(string devmPath, bool skipMcp, bool town = true)`.

- [ ] **Step 1: Write failing tests:**
  - `AddTownHooks_AddsEachEvent`: each of `TownEvents` gets one entry `{ "hooks": [ { "type":"command", "command":"\"C:\\x\\devm.exe\" town-hook", "timeout": 2 } ] }`; `PermissionRequest` gets the same with `"timeout": 600`. The user's existing `PreToolUse` `Write` matcher and comment-free content are kept.
  - `AddTownHooks_Idempotent`: second call returns false.
  - `RemoveTownHooks_LeavesPreToolUseHookAndOthers`: after `AddHook` + `AddTownHooks` + `RemoveTownHooks`, the dev-server `PreToolUse` hook is still there and no `town-hook` command remains; empty arrays are removed.
  - `RemoveHook_DoesNotTouchTownHooks`: the existing `IsOurHook` must not match `town-hook` commands.
  - `Setup_NoTown_SkipsTownHooks` and `Remove_RemovesTownHooks` through the real files in `_dir` with `CLAUDE_CONFIG_DIR`.
- [ ] **Step 2: Run** `dotnet test --filter ClaudeSetupTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Recognise town hooks by a command ending in ` town-hook`. `Setup` with `town` adds them and prints `town: added hooks` / `town: already present`; `Remove` always removes them. `install.ps1` needs no change (it already calls `claude-setup`).
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(town): claude-setup installs the Agent Town hooks`.

### Task 8: Live check against a real session

**Files:** none (verification only).

- [ ] **Step 1:** Ask the user to approve installing the new build and adding the town hooks to the real `~/.claude/settings.json` (`devm claude-setup --no-mcp`). Do not proceed without that approval; `claude-setup` backs settings up first.
- [ ] **Step 2:** Start a desktop Claude session in a registered repo. `GET /api/town/sessions` (with the token) shows it with `surface:"desktop"` and `canSend:true`.
- [ ] **Step 3:** `POST /api/town/send` with text `Reply with the single word PINEAPPLE.`; the session answers `PINEAPPLE`.
- [ ] **Step 4:** With focus false, trigger a permission prompt (a Bash command not allowed by rules in a session in default permission mode): the session window shows its normal dialog with no delay.
- [ ] **Step 5:** Report results to the user; no commit.
